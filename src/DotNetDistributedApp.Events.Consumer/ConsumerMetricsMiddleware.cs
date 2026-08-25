using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using KafkaFlow;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

/// <summary>
/// Records the outcome of every consume attempt: <c>events.consume_success</c>, <c>events.consume_failed</c> and
/// <c>events.consume_unrecognised</c>.
/// </summary>
/// <remarks>
/// <para>
/// Its position in the pipeline is pinned by two constraints that intersect at exactly one place — between the
/// deserializer and <see cref="WeatherDeduplicationMiddleware"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Inside the deserializer</b>, because all three counters are tagged by <c>event_name</c> and that only exists once
/// the payload has been deserialized. <c>DeserializerConsumerMiddleware</c> passes the deserialized value inward on a
/// <b>new</b> <see cref="IMessageContext"/>, so a middleware registered outside it holds raw bytes and still does after
/// <c>next</c> returns.
/// </item>
/// <item>
/// <b>Inside <see cref="RetryDeadLetterMiddleware"/></b>, which swallows the exception once it has successfully routed a
/// message to the dead letter topic. Anything outside it would count a poison message as a success.
/// </item>
/// </list>
/// <para>
/// <c>events.consume_failed</c> is the main <i>metric</i> signal that a handler failed, because KafkaFlow's
/// <c>ConsumerWorker.ProcessMessageAsync</c> catches every non-cancellation exception and stores the offset anyway. It
/// cannot see a deserialization failure — that happens outside this middleware — but those are dead lettered rather than
/// dropped, so the dead letter topic is the signal for them.
/// </para>
/// <para>
/// Left on the default <c>MiddlewareLifetime.ConsumerOrProducer</c> deliberately: both dependencies are thread-safe
/// singletons and all per-message state is local, so one instance shared across the consumer's workers is correct. This
/// is the opposite of <see cref="WeatherDeduplicationMiddleware"/>, which injects a <c>DbContext</c> and therefore must
/// be per message.
/// </para>
/// </remarks>
public partial class ConsumerMetricsMiddleware(
    IMetricsService metricsService,
    ILogger<ConsumerMetricsMiddleware> logger
) : IMessageMiddleware
{
    public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
    {
        var topic = context.ConsumerContext.Topic;

        if (context.Message.Value is not BaseEventPayloadDto payload)
        {
            // Tagged by CLR type because there is no EventName to read. Skipping the handlers rather than throwing
            // leaves the offset to be stored, so the message is not redelivered - there is nothing downstream that
            // could do anything with it, and StrictMessageTypeResolver already dead letters the cases that are defects
            // rather than merely unknown.
            LogUnrecognisedMessageSkipped(
                context.Message.Value?.GetType().FullName ?? "null",
                context.ConsumerContext.Offset
            );
            metricsService.ConsumeEventUnrecognised(1, topic, context.Message.Value?.GetType().Name ?? "unknown");
            return;
        }

        try
        {
            await next(context);
            // Only reached when the handlers genuinely completed, so success + failed = attempts and success never
            // lies about a poison message. A duplicate skipped by WeatherDeduplicationMiddleware counts as a success -
            // it was consumed without error - with events.consume_duplicate as the orthogonal dimension.
            metricsService.ConsumeEventSuccess(1, topic, payload.EventName);
        }
        catch (OperationCanceledException) when (context.ConsumerContext.WorkerStopped.IsCancellationRequested)
        {
            // Shutdown, not a consume failure. Mirrors the same carve-out in RetryDeadLetterMiddleware.
            throw;
        }
        catch
        {
            metricsService.ConsumeEventFailed(1, topic, payload.EventName);
            throw; // RetryDeadLetterMiddleware is outside this middleware; swallowing here would cost the message its retries and its dead letter
        }
    }

    [LoggerMessage(LogLevel.Warning, "Unrecognised message skipped: {MessageType} at offset {Offset}")]
    private partial void LogUnrecognisedMessageSkipped(string messageType, long offset);
}
