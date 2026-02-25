using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;

namespace DotNetDistributedApp.Events.Consumer;

public class FailingEventMessageHandler : IMessageHandler<FailingEventPayloadDto>
{
    public Task Handle(IMessageContext context, FailingEventPayloadDto message)
    {
        Console.WriteLine(
            "Partition: {0} | Offset: {1} | Message: {2}",
            context.ConsumerContext.Partition,
            context.ConsumerContext.Offset,
            message.EventName
        );

        // Simulate failed event processing
        throw new ArgumentException("Simulated exception");
    }
}
