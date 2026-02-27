using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class FailingEventMessageHandler(ILogger<FailingEventMessageHandler> logger)
    : IMessageHandler<FailingEventPayloadDto>
{
    public Task Handle(IMessageContext context, FailingEventPayloadDto message)
    {
        Console.WriteLine(
            "Partition: {0} | Offset: {1} | Message: {2}",
            context.ConsumerContext.Partition,
            context.ConsumerContext.Offset,
            message.EventName
        );

        LogHandlingFailingEvent(message.EventName);

        // Simulate failed event processing
        throw new ArgumentException("Simulated exception");
    }

    [LoggerMessage(LogLevel.Information, "Handling failing event: {EventName}")]
    private partial void LogHandlingFailingEvent(string eventName);
}
