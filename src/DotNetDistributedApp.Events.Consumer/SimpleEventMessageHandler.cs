using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;

namespace DotNetDistributedApp.Events.Consumer;

public class SimpleEventMessageHandler : IMessageHandler<SimpleEventPayloadDto>
{
    public Task Handle(IMessageContext context, SimpleEventPayloadDto message)
    {
        Console.WriteLine(
            "Partition: {0} | Offset: {1} | Message: {2}",
            context.ConsumerContext.Partition,
            context.ConsumerContext.Offset,
            message.EventName
        );

        return Task.CompletedTask;
    }
}
