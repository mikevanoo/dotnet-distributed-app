using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class FailingEventMessageHandler(ILogger<FailingEventMessageHandler> logger)
    : IMessageHandler<FailingEventPayloadDto>
{
    public Task Handle(IMessageContext context, FailingEventPayloadDto message)
    {
        LogHandlingFailingEvent(message.EventName, message.PartitionKey);

        // Simulate failed event processing
        throw new ArgumentException("Simulated exception");
    }

    [LoggerMessage(LogLevel.Information, "Handling failing event: {EventName} {PartitionKey}")]
    private partial void LogHandlingFailingEvent(string eventName, string partitionKey);
}
