using System.Collections.Concurrent;
using System.Text;
using DotNetDistributedApp.Events.Consumer;
using KafkaFlow;

namespace DotNetDistributedApp.IntegrationTests.EventsConsumer;

/// <summary>
/// Records every message arriving on <c>common-dlq</c> by its raw bytes, so a test can await a dead letter that cannot
/// be deserialized at all.
/// </summary>
/// <remarks>
/// <see cref="DeadLetterRecorder"/> cannot serve this: it is fed by a typed handler, and a message whose
/// <c>Message-Type</c> header is missing or whose payload is malformed never reaches one. Keying on the exact payload
/// string also makes the wait itself an assertion of fidelity — <see cref="RetryDeadLetterMiddleware"/> forwards the
/// original bytes untouched, so a re-serialized or base64'd dead letter would never match.
/// </remarks>
public class UnreadableDeadLetterRecorder
{
    private readonly ConcurrentDictionary<
        (string ConsumerGroup, string Payload),
        TaskCompletionSource<IMessageHeaders>
    > _deadLettered = new();

    public void Record(string consumerGroup, string payload, IMessageHeaders headers) =>
        Completion(consumerGroup, payload).TrySetResult(headers);

    /// <summary>Completes with the headers the dead letter carried, so a test can assert what survived the hop.</summary>
    public Task<IMessageHeaders> WaitFor(string consumerGroup, string payload, CancellationToken cancellationToken) =>
        Completion(consumerGroup, payload).Task.WaitAsync(cancellationToken);

    // GetOrAdd both ways round, so it does not matter whether the dead letter arrives before or after the wait starts.
    private TaskCompletionSource<IMessageHeaders> Completion(string consumerGroup, string payload) =>
        _deadLettered.GetOrAdd(
            (consumerGroup, payload),
            _ => new TaskCompletionSource<IMessageHeaders>(TaskCreationOptions.RunContinuationsAsynchronously)
        );
}

/// <summary>
/// Feeds <see cref="UnreadableDeadLetterRecorder"/>. Registered on the fixture's <c>common-dlq</c> consumer
/// <b>before</b> its deserializer, which is the only position that sees a message the deserializer would reject.
/// </summary>
public class UnreadableDeadLetterRecordingMiddleware(UnreadableDeadLetterRecorder recorder) : IMessageMiddleware
{
    public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
    {
        if (context.Message.Value is byte[] rawPayload)
        {
            recorder.Record(
                context.Headers.GetString(DeadLetterHeaders.ConsumerGroup) ?? string.Empty,
                Encoding.UTF8.GetString(rawPayload),
                context.Headers
            );
        }

        await next(context);
    }
}

/// <summary>
/// Marker type for a producer with no serializer middleware, so a test can put arbitrary bytes on
/// <see cref="Api.Common.Events.Topics.Common"/> — including bytes no consumer can deserialize.
/// </summary>
public sealed class UnreadableEventProducer;
