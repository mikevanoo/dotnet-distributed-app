using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotNetDistributedApp.Events.Consumer;

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
            if (context.Message.Value is not BaseEventPayloadDto payload)
            {
                throw new ArgumentException(
                    $"Message value is not BaseEventPayloadDto, cannot send to DLQ. Type: {context.Message.Value?.GetType().FullName ?? "null"}"
                );
            }

            var partitionKey = payload.PartitionKey;
            await dlqProducer.ProduceAsync(Topics.CommonDlq, partitionKey, payload);
            LogMessageSentToDeadLetterTopic(Topics.CommonDlq, partitionKey, failure.Message);
        }
        catch (Exception ex)
        {
            LogFailedToProduceMessageToDeadLetterTopic(ex);
            throw;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Message handling failed (attempt {Attempt}/{MaxRetries})")]
    private partial void LogMessageHandlingFailed(Exception ex, int attempt, int maxRetries);

    [LoggerMessage(LogLevel.Information, "Retrying in {DelayMs}ms")]
    private partial void LogRetryingIn(double delayMs);

    [LoggerMessage(
        LogLevel.Warning,
        "Message sent to dead letter topic {Topic} after exhausting retries. PartitionKey: {PartitionKey}, Error: {Error}"
    )]
    private partial void LogMessageSentToDeadLetterTopic(string topic, string partitionKey, string error);

    [LoggerMessage(LogLevel.Error, "Failed to produce message to DLQ; rethrowing so offset is not committed")]
    private partial void LogFailedToProduceMessageToDeadLetterTopic(Exception ex);
}

/// <summary>Marker type for the DLQ producer dependency injection.</summary>
public sealed class DlqProducer;
