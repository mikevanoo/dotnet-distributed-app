using DotNetDistributedApp.Api.Common.Metrics;
using KafkaFlow;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Api.Common.Events;

public partial class EventsService(
    IMessageProducer<EventsService> producer,
    IMetricsService metricsService,
    ILogger<EventsService> logger
) : IEventsService
{
    public async Task SendEvent(string topic, BaseEventPayloadDto payload)
    {
        try
        {
            await producer.ProduceAsync(topic, payload.PartitionKey, payload);
            metricsService.ProduceEventSuccess(1, topic, payload.EventName);
        }
        catch (Exception ex)
        {
            LogFailedToSendEvent(ex, topic, payload.EventName, payload.PartitionKey);
            metricsService.ProduceEventFailed(1, topic, payload.EventName);
        }
    }

    [LoggerMessage(
        LogLevel.Error,
        "Failed to send event: Topic:{Topic}, Name={EventName}, PartitionKey:{PartitionKey}"
    )]
    private partial void LogFailedToSendEvent(Exception ex, string topic, string eventName, string partitionKey);
}
