using Confluent.Kafka;

namespace DotNetDistributedApp.Api.Events;

public class EventsService<T>(IProducer<string, T> producer) : IEventsService<T>
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
        var deliveryResult = await producer.ProduceAsync(topic, message);
        // deliveryResult.Status
    }
}
