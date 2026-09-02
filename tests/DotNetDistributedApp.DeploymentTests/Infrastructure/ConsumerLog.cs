using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>
/// Reads the deployed consumer's log, and waits for the one event in it that a test can recognise
/// without knowing an id: a dead letter.
/// </summary>
/// <remarks>
/// <para>
/// Topic <c>common</c> has a single partition, so the consumer handles messages strictly in order and
/// one failing event occupies it for the whole retry ladder - roughly eighteen seconds of backoff.
/// Several tests publish one, so a failing event can sit behind two others before its own ladder even
/// begins. Hence the generous timeout, and hence waiting for the dead letter rather than sleeping for a
/// fixed interval and hoping.
/// </para>
/// <para>
/// Waiting is also what stops an absence assertion from passing vacuously: "no inbox row for
/// failing-event" is trivially true before the handler has run at all.
/// </para>
/// </remarks>
public static class ConsumerLog
{
    /// <summary>Long enough for this test's failing event to queue behind every other one in the suite.</summary>
    public static readonly TimeSpan DeadLetterTimeout = TimeSpan.FromMinutes(3);

    private const string DeadLetterMarker = "dead letter topic";

    /// <summary>
    /// Polls the consumer log until a dead letter appears in it, and returns the whole window so the
    /// caller can assert on the attempts that led up to it.
    /// </summary>
    public static Task<string> WaitForDeadLetterAsync(
        ClusterFixture clusterFixture,
        DateTimeOffset since,
        CancellationToken cancellationToken
    ) =>
        Eventually.ForAsync(
            $"a failing event to be dead lettered to {Topics.CommonDlq}",
            token => ReadSinceAsync(clusterFixture, since, token),
            log => log.Contains($"{DeadLetterMarker} {Topics.CommonDlq}", StringComparison.Ordinal),
            DeadLetterTimeout,
            cancellationToken,
            pollInterval: TimeSpan.FromSeconds(2)
        );

    /// <summary>
    /// The consumer log from <paramref name="since" /> onwards. Bounding the window is what makes
    /// matching on log text meaningful: over the whole log, any marker would match some earlier event.
    /// </summary>
    public static Task<string> ReadSinceAsync(
        ClusterFixture clusterFixture,
        DateTimeOffset since,
        CancellationToken cancellationToken
    ) =>
        clusterFixture.GetPodLogAsync(
            ResourceNames.EventsConsumer,
            cancellationToken,
            tailLines: null,
            // Rounded up by a second so the window cannot start after the first attempt was logged.
            sinceSeconds: (int)Math.Ceiling((DateTimeOffset.UtcNow - since).TotalSeconds) + 1
        );
}
