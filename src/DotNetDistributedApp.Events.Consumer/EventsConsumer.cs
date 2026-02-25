using System.Globalization;
using Confluent.Kafka;
using DotNetDistributedApp.Api.Common;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class EventsConsumer(
    IConsumer<string, BaseEventPayloadDto> eventConsumer,
    IEventsService<BaseEventPayloadDto> eventsService,
    IServiceProvider serviceProvider,
    IMetricsService metricsService,
    IDateTimeProvider dateTimeProvider,
    ILogger<EventsConsumer> logger
)
{
    private const int CommitBatchSize = 10;
    private int _messageCount;

    public async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogSubscribingToTopic(Topics.Common);
        eventConsumer.Subscribe(Topics.Common);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // consumption and processing is split so we can ensure that we have a message before we attempt to process it
                ConsumeResult<string, BaseEventPayloadDto> consumeResult;
                try
                {
                    consumeResult = eventConsumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    LogConsumeError(ex);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }

                try
                {
                    var topic = consumeResult.Topic;
                    var eventName = consumeResult.Message.Value.EventName;

                    await ProcessMessageAsync(consumeResult.Message.Value, topic, eventName, stoppingToken);

                    _messageCount++;
                    if (_messageCount >= CommitBatchSize)
                    {
                        eventConsumer.Commit(consumeResult);
                        _messageCount = 0;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is requested
                    break;
                }
                catch (Exception ex)
                {
                    HandleProcessingError(ex, consumeResult, stoppingToken);
                }
            }
        }
        finally
        {
            LogClosingConsumer();
            eventConsumer.Close();
        }
    }

    private async Task ProcessMessageAsync(
        BaseEventPayloadDto payload,
        string topic,
        string eventName,
        CancellationToken cancellationToken
    )
    {
        if (!BaseEventPayloadDto.EventPayloadMap.TryGetValue(eventName, out var eventType))
        {
            metricsService.ConsumeEventUnrecognised(1, topic, eventName);
            LogUnrecognisedEvent(eventName);
            return;
        }
        var specificPayload = Convert.ChangeType(payload, eventType, CultureInfo.InvariantCulture);

        // Create a fresh instance of a scoped handler so they can, in-turn, use scoped dependencies
        var handlerType = typeof(IEventHandler<>).MakeGenericType(eventType);
        using var scope = serviceProvider.CreateScope();
        var handler = scope.ServiceProvider.GetService(handlerType);
        if (handler == null)
        {
            LogNoHandlerFound(eventName, eventType.Name);
            metricsService.ConsumeEventUnrecognised(1, topic, eventName);
            return;
        }

        // Invoke the handler
        var handleMethod = handlerType.GetMethod("HandleAsync");
        if (handleMethod != null)
        {
            await (Task)handleMethod.Invoke(handler, [specificPayload, cancellationToken])!;
            metricsService.ConsumeEventSuccess(1, topic, eventName);
            LogConsumedMessage(eventName);
        }
    }

    private void HandleProcessingError(
        Exception ex,
        ConsumeResult<string, BaseEventPayloadDto> consumeResult,
        CancellationToken cancellationToken
    )
    {
        const int FailureLimit = 3;

        var targetTopic = consumeResult.Topic;
        var message = consumeResult.Message;
        var payload = message.Value;
        var eventName = payload.EventName;
        var partitionKey = payload.PartitionKey;
        var payloadRetry = payload.Retry;

        LogProcessingError(ex, eventName, partitionKey);

        payloadRetry.TargetTopic = targetTopic;
        payloadRetry.FailedCount++;
        if (payloadRetry.FailedCount <= FailureLimit)
        {
            LogSendingToOutOfOrder(eventName, partitionKey, payloadRetry.FailedCount);
            if (payloadRetry.FailedCount == 1)
            {
                payloadRetry.FirstFailureTimestamp = dateTimeProvider.UtcNow;
            }

            eventsService.SendEvent(Topics.OutOfOrder, payload);
        }
        else
        {
            // Log
            // DLQ
        }
    }

    [LoggerMessage(LogLevel.Information, "Subscribing to topic: {Topic}")]
    private partial void LogSubscribingToTopic(string topic);

    [LoggerMessage(LogLevel.Information, "Consumed message: {EventName}")]
    private partial void LogConsumedMessage(string eventName);

    [LoggerMessage(LogLevel.Warning, "Unrecognised event: {EventName}")]
    private partial void LogUnrecognisedEvent(string eventName);

    [LoggerMessage(LogLevel.Error, "No handler found for event: {EventName} (Type: {EventType})")]
    private partial void LogNoHandlerFound(string eventName, string eventType);

    [LoggerMessage(LogLevel.Error, "Error consuming message")]
    private partial void LogConsumeError(Exception ex);

    [LoggerMessage(LogLevel.Error, "Error processing message: {EventName} (PartitionKey: {PartitionKey})")]
    private partial void LogProcessingError(Exception ex, string eventName, string partitionKey);

    [LoggerMessage(
        LogLevel.Information,
        "Sending event to out-of-order topic: {EventName} (PartitionKey: {PartitionKey}, FailedCount: {FailedCount})"
    )]
    private partial void LogSendingToOutOfOrder(string eventName, string partitionKey, int failedCount);

    [LoggerMessage(LogLevel.Information, "Closing consumer")]
    private partial void LogClosingConsumer();
}
