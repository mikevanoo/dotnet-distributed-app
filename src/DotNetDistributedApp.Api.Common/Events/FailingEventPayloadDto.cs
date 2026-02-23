namespace DotNetDistributedApp.Api.Common.Events;

public class FailingEventPayloadDto : BaseEventPayloadDto
{
    public override string EventName => "failing-event";

    public FailingEventPayloadDto(string partitionKey)
        : base(partitionKey) { }

    public FailingEventPayloadDto() { }
}
