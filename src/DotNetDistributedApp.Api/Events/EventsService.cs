using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;

namespace DotNetDistributedApp.Api.Events;

public partial class EventsService<T>(
    IProducer<string, T> producer,
    IMetricsService metricsService,
    ILogger<EventsService<T>> logger
) : IEventsService<T>
    where T : BaseEventPayloadDto
{
    public async Task SendEvent(string topic, T payload)
    {
        var message = new Message<string, T>
        {
            Timestamp = Timestamp.Default,
            Key = payload.PartitionKey,
            Value = payload,
        };

        try
        {
            await producer.ProduceAsync(topic, message);
            metricsService.ProduceEventSuccess(1, topic, payload.EventName);
        }
        catch (ProduceException<string, T> ex)
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
