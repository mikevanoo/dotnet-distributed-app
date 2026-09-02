using System.Net.Http.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.DeploymentTests.Infrastructure;

namespace DotNetDistributedApp.DeploymentTests.Events;

/// <summary>
/// The deployed consumer's transactional inbox, read straight out of Postgres. This is the end of the
/// event path: the API published to Kafka, the consumer handled the message, and the row proves the
/// handler's work and the "processed" record committed together.
/// </summary>
/// <remarks>
/// <para>
/// Assertions are count deltas rather than lookups by identity, because the API generates the event id
/// and partition key server-side - nothing in the HTTP response identifies the row the call will
/// produce. That is also why this class runs serially with the rest of the assembly.
/// </para>
/// <para>
/// <c>ProcessedWeatherEventsCleaner</c> deletes from this table every minute across all consumer
/// groups, but only rows past their retention - a day by default - so rows created during a test run
/// are not at risk. Shortening that retention would make these assertions flaky.
/// </para>
/// </remarks>
public class TransactionalInboxShould(ClusterFixture clusterFixture)
{
    private static readonly TimeSpan ConsumeTimeout = TimeSpan.FromSeconds(90);

    private const string SimpleEventName = "simple-event";
    private const string FailingEventName = "failing-event";

    [DeploymentFact]
    public async Task RecordAHandledEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inbox = await DeployedInbox.ConnectAsync(clusterFixture, cancellationToken);
        var before = await inbox.CountAsync(SimpleEventName, cancellationToken);

        await PublishAsync("/v1/events/simple-event", cancellationToken);

        var after = await Eventually.ForAsync(
            $"one new '{SimpleEventName}' row in the inbox",
            token => inbox.CountAsync(SimpleEventName, token),
            count => count >= before + 1,
            ConsumeTimeout,
            cancellationToken
        );
        after.Should().Be(before + 1);
    }

    /// <summary>
    /// The duplicate endpoint publishes one payload twice, so the inbox must skip the second: two
    /// messages consumed, one row written. This is the whole point of the inbox - Kafka's commit window
    /// makes redelivery of an already-handled message normal rather than exceptional.
    /// </summary>
    [DeploymentFact]
    public async Task RecordADuplicatedEventOnlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inbox = await DeployedInbox.ConnectAsync(clusterFixture, cancellationToken);
        var before = await inbox.CountAsync(SimpleEventName, cancellationToken);

        await PublishAsync("/v1/events/duplicate-event", cancellationToken);

        await Eventually.UntilAsync(
            $"the first copy of the duplicated '{SimpleEventName}' to be recorded",
            async token => await inbox.CountAsync(SimpleEventName, token) >= before + 1,
            ConsumeTimeout,
            cancellationToken
        );

        // Give the second copy time to be consumed and skipped before asserting nothing else landed.
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        (await inbox.CountAsync(SimpleEventName, cancellationToken)).Should().Be(before + 1);
    }

    /// <summary>
    /// A dead-lettered event is never recorded as processed: the deduplication middleware's transaction
    /// rolls back on every attempt, so no row survives however many times the handler ran.
    /// </summary>
    /// <remarks>
    /// The wait is for the dead letter itself rather than a fixed delay. An assertion that a row is
    /// absent passes for free if the handler has not run yet, and on a single partition this event can
    /// be queued behind another test's for some time.
    /// </remarks>
    [DeploymentFact]
    public async Task NeverRecordADeadLetteredEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var inbox = await DeployedInbox.ConnectAsync(clusterFixture, cancellationToken);
        var before = await inbox.CountAsync(FailingEventName, cancellationToken);
        var startedAt = DateTimeOffset.UtcNow;

        using var httpClient = clusterFixture.CreateApiClient();
        var response = await httpClient.PostAsync("/v1/events/failing-event", null, cancellationToken);
        response.Should().Be200Ok();

        await ConsumerLog.WaitForDeadLetterAsync(clusterFixture, startedAt, cancellationToken);
        (await inbox.CountAsync(FailingEventName, cancellationToken)).Should().Be(before);
    }

    private async Task PublishAsync(string path, CancellationToken cancellationToken)
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.PostAsJsonAsync(
            path,
            new { value = $"deployment-test-{Guid.CreateVersion7()}" },
            cancellationToken
        );

        response.Should().Be200Ok();
    }
}
