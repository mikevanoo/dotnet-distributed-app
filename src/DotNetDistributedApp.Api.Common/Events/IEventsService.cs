namespace DotNetDistributedApp.Api.Common.Events;

public interface IEventsService
{
    public Task SendEvent(string topic, BaseEventPayloadDto payload);
}
