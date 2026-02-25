namespace DotNetDistributedApp.Api.Common.Events;

public interface IEventsService
{
    public Task SendEvent(string topic, BaseEventPayloadDto payload);
}

public interface IEventsService<T>
    where T : BaseEventPayloadDto
{
    public Task SendEvent(string topic, T payload);
}
