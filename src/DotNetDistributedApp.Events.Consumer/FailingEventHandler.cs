using DotNetDistributedApp.Api.Common.Events;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class FailingEventHandler(ILogger<FailingEventHandler> logger)
    : IEventHandler<FailingEventPayloadDto>
{
    public Task HandleAsync(FailingEventPayloadDto payload, CancellationToken cancellationToken)
    {
        LogHandlingFailingEvent(payload.EventName);

        // Add your business logic here

        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "Handling failing event: {EventName}")]
    private partial void LogHandlingFailingEvent(string eventName);
}
