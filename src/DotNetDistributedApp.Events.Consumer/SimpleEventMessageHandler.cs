using System.Text.Json;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class SimpleEventMessageHandler(ILogger<SimpleEventMessageHandler> logger)
    : IMessageHandler<SimpleEventPayloadDto>
{
    public Task Handle(IMessageContext context, SimpleEventPayloadDto message)
    {
        var valueJson = JsonSerializer.Serialize(message);
        LogHandlingSimpleEvent(valueJson);

        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "Handling simple event: {Value}")]
    private partial void LogHandlingSimpleEvent(string value);
}
