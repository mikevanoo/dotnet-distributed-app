using System.Collections.Concurrent;

namespace DotNetDistributedApp.IntegrationTests.EventsConsumer;

/// <summary>
/// Records the events that arrive on <c>common-dlq</c>, so a test can await one as a completion barrier.
/// </summary>
/// <remarks>
/// Dead lettering is the terminal state of an event whose handler keeps throwing, and it is the only observable
/// signal that the pipeline is finished with such an event — the failure path deliberately writes nothing to the
/// database. Await it before asserting that nothing was written; polling cannot establish an absence.
/// <para>
/// Keyed by consumer group as well as event id: several groups consume <c>common</c> during a test run and each
/// dead letters its own copy, so a test must wait for the one produced by the pipeline it is asserting on.
/// </para>
/// </remarks>
public class DeadLetterRecorder
{
    private readonly ConcurrentDictionary<(string ConsumerGroup, Guid EventId), TaskCompletionSource> _deadLettered =
        new();

    public void Record(string consumerGroup, Guid eventId) => Completion(consumerGroup, eventId).TrySetResult();

    public Task WaitFor(string consumerGroup, Guid eventId, CancellationToken cancellationToken) =>
        Completion(consumerGroup, eventId).Task.WaitAsync(cancellationToken);

    // GetOrAdd both ways round, so it does not matter whether the dead letter arrives before or after the wait starts.
    private TaskCompletionSource Completion(string consumerGroup, Guid eventId) =>
        _deadLettered.GetOrAdd(
            (consumerGroup, eventId),
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        );
}
