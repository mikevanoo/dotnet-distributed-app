using System.Text;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetDistributedApp.Events.Consumer;

/// <summary>
/// Retries a failing message in process and dead letters it once the retries are exhausted.
/// </summary>
/// <remarks>
/// Registered as the <b>outermost</b> consumer middleware, outside the deserializer — see
/// <see cref="ServiceCollectionExtensions.AddEventsConsumerKafka"/> for why. Two consequences follow from that
/// position:
/// <list type="bullet">
/// <item>
/// This middleware never sees a deserialized payload. <c>DeserializerConsumerMiddleware</c> passes the deserialized
/// value inward on a <b>new</b> <see cref="IMessageContext"/> and never mutates this one, so
/// <c>context.Message.Value</c> stays the raw Kafka bytes for the whole retry loop. The dead letter is therefore a
/// byte-for-byte copy of the original message plus one header, which is both higher fidelity than re-serializing a DTO
/// and the only shape that works for a payload that never deserialized.
/// </item>
/// <item>
/// A deserialization failure is deterministic, so its retries can never succeed — it costs the configured backoff
/// before dead lettering. Accepted in exchange for one uniform failure path; the alternative is inspecting exception
/// types to decide what is worth retrying.
/// </item>
/// </list>
/// </remarks>
public partial class RetryDeadLetterMiddleware(
    IMessageProducer<DlqProducer> dlqProducer,
    IOptions<RetryDeadLetterOptions> options,
    ILogger<RetryDeadLetterMiddleware> logger
) : IMessageMiddleware
{
    public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
    {
        var opts = options.Value;
        var attempt = 0;
        Exception? lastException;

        while (true)
        {
            try
            {
                attempt++;
                await next(context);
                return;
            }
            catch (OperationCanceledException) when (context.ConsumerContext.WorkerStopped.IsCancellationRequested)
            {
                // propagate cancellation immediately, instead of being counted as a failed retry attempt or triggering DLQ routing
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                LogMessageHandlingFailed(ex, attempt, opts.MaxRetryCount + 1);

                if (attempt > opts.MaxRetryCount)
                {
                    break;
                }

                var delay = opts.GetDelay(attempt);
                LogRetryingIn(delay.TotalMilliseconds);
                await Task.Delay(delay, context.ConsumerContext.WorkerStopped);
            }
        }

        // All retries exhausted: send to DLQ and complete (so offset is committed and message is not redelivered)
        await SendToDeadLetterAsync(context, lastException!);
    }

    private async Task SendToDeadLetterAsync(IMessageContext context, Exception failure)
    {
        try
        {
            // Copied rather than mutated in place: the original headers are the consumer's, and carrying them over is
            // what keeps the dead letter deserializable - Message-Type among them names the payload's CLR type.
            var headers = new MessageHeaders();
            foreach (var (key, value) in context.Headers)
            {
                headers.Add(key, value);
            }
            headers.SetString(DeadLetterHeaders.ConsumerGroup, context.ConsumerContext.GroupId);

            // The raw key and value, unchanged. The DLQ producer deliberately has no serializer middleware, so
            // KafkaFlow produces these bytes as they arrived.
            await dlqProducer.ProduceAsync(Topics.CommonDlq, context.Message.Key, context.Message.Value, headers);
            LogMessageSentToDeadLetterTopic(Topics.CommonDlq, DescribeKey(context.Message.Key), failure.Message);
        }
        catch (Exception ex)
        {
            /*
             * Setting ShouldStoreOffset = false is what keeps the offset uncommitted. Rethrowing does not:
             * KafkaFlow's ConsumerWorker.ProcessMessageAsync catches every non-cancellation exception, logs it, and its
             * finally block still calls ConsumerContext.Complete() because AutoMessageCompletion defaults to true - so
             * without this line a message the dead letter topic refused would be silently lost.
             *
             * Cost of getting it right: KafkaFlow's OffsetManager commits contiguously, so a context that is never
             * marked processed pins this partition's committed offset forever. Later messages keep being processed but
             * their offsets accumulate unbounded in memory, and on restart everything from here is redelivered (the
             * deduplication inbox absorbs the replay). That is the correct trade against losing the message, but it
             * makes a failing DLQ produce an incident to act on, not a warning to skim.
             */
            context.ConsumerContext.ShouldStoreOffset = false;
            LogFailedToProduceMessageToDeadLetterTopic(ex);
            throw;
        }
    }

    /// <summary>Renders a raw Kafka message key for logging. Keys are produced as UTF-8 encoded strings.</summary>
    private static string DescribeKey(object? key) =>
        key switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            null => "null",
            _ => key.ToString() ?? "null",
        };

    [LoggerMessage(LogLevel.Warning, "Message handling failed (attempt {Attempt}/{MaxRetries})")]
    private partial void LogMessageHandlingFailed(Exception ex, int attempt, int maxRetries);

    [LoggerMessage(LogLevel.Information, "Retrying in {DelayMs}ms")]
    private partial void LogRetryingIn(double delayMs);

    [LoggerMessage(
        LogLevel.Warning,
        "Message sent to dead letter topic {Topic} after exhausting retries. MessageKey: {MessageKey}, Error: {Error}"
    )]
    private partial void LogMessageSentToDeadLetterTopic(string topic, string messageKey, string error);

    [LoggerMessage(
        LogLevel.Error,
        "Failed to produce message to DLQ. The offset will not be stored, so this partition stops committing and the "
            + "message is redelivered after a restart - the consumer cannot make durable progress until the DLQ accepts writes."
    )]
    private partial void LogFailedToProduceMessageToDeadLetterTopic(Exception ex);
}

/// <summary>Marker type for the DLQ producer dependency injection.</summary>
public sealed class DlqProducer;

/// <summary>Headers <see cref="RetryDeadLetterMiddleware"/> sets on the messages it dead letters.</summary>
public static class DeadLetterHeaders
{
    /// <summary>
    /// The consumer group that exhausted its retries. Every group subscribed to <see cref="Topics.Common"/> gets its
    /// own copy of every message and runs its own copy of this pipeline, so the dead letter topic can hold one message
    /// per group for a single event. Without this header they are indistinguishable.
    /// </summary>
    public const string ConsumerGroup = "dead-letter-consumer-group";
}
