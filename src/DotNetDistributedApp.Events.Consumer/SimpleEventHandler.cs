using System.Text.Json;
using DotNetDistributedApp.Api.Common.Events;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class SimpleEventHandler(ILogger<SimpleEventHandler> logger)
    : IEventHandler<SimpleEventPayloadDto>
{
    public Task HandleAsync(SimpleEventPayloadDto payload, CancellationToken cancellationToken)
    {
        var valueJson = JsonSerializer.Serialize(payload);
        LogHandlingSimpleEvent(valueJson);

        // Add your business logic here

        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "Handling simple event: {Value}")]
    private partial void LogHandlingSimpleEvent(string value);
}
