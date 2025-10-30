namespace DotNetDistributedApp.Api.Events;

public interface IEventsService<T>
    where T : BaseEventPayloadDto
{
    public Task SendEvent(string topic, T payload);
}
