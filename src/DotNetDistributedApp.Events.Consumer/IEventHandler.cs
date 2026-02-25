using DotNetDistributedApp.Api.Common.Events;

namespace DotNetDistributedApp.Events.Consumer;

// TODO remove
public interface IEventHandler<in T>
    where T : BaseEventPayloadDto
{
    Task HandleAsync(T payload, CancellationToken cancellationToken);
}
