using DotNetDistributedApp.Api.Common.Events;

namespace DotNetDistributedApp.Events.Consumer;

public interface IEventHandler<in T> where T : BaseEventPayloadDto
{
    Task HandleAsync(T payload, CancellationToken cancellationToken);
}
