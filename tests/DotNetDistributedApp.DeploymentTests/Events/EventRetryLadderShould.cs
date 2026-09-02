using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Events;

/// <summary>
/// The consumer's retry ladder and dead letter queue, as seen from the consumer's pod log.
/// </summary>
/// <remarks>
/// <para>
/// The log is the signal here rather than the DLQ topic itself, because a Kafka client on the host
/// cannot usefully read <c>common-dlq</c>: the broker advertises its in-cluster service address, so a
/// consumer reached through a port forward is handed a broker address it cannot resolve.
/// </para>
/// <para>
/// String matching on a log is the most brittle thing in this suite, so it is bounded rather than
/// loosened - see <see cref="ConsumerLog" />. The complementary assertion, that a dead lettered event
/// is never recorded as processed, is in
/// <see cref="TransactionalInboxShould.NeverRecordADeadLetteredEvent" /> and reads the database
/// instead.
/// </para>
/// </remarks>
public class EventRetryLadderShould(ClusterFixture clusterFixture)
{
    private const int ExpectedAttempts = 4;

    [DeploymentFact]
    public async Task ExhaustEveryRetryAttemptThenDeadLetterTheMessage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var startedAt = DateTimeOffset.UtcNow;

        using var httpClient = clusterFixture.CreateApiClient();
        var response = await httpClient.PostAsync("/v1/events/failing-event", null, cancellationToken);
        response.Should().Be200Ok();

        var log = await ConsumerLog.WaitForDeadLetterAsync(clusterFixture, startedAt, cancellationToken);

        // Every attempt is logged, and the handler runs once per attempt rather than once in total.
        foreach (var attempt in Enumerable.Range(1, ExpectedAttempts))
        {
            log.Should().Contain($"attempt {attempt}/{ExpectedAttempts}");
        }

        log.Should().Contain(Topics.CommonDlq);

        // The ladder swallows the exception once the message is dead lettered, so nothing above it ever
        // sees a failure - which is why the consumer survives a poison message rather than crash-looping
        // on it. Asserted here rather than in its own test: a second failing event would add another
        // eighteen seconds to the single partition's queue to prove the same thing.
        var pod = await clusterFixture.GetRunningPodAsync(ResourceNames.EventsConsumer, cancellationToken);
        pod.Status.ContainerStatuses.Should().OnlyContain(containerStatus => containerStatus.Ready);
    }
}
