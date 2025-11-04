namespace DotNetDistributedApp.Api.Common.Events;

public class Event1PayloadDto : BaseEventPayloadDto
{
    public override string EventName => "event1";

    public string? Value { get; set; }

    public Event1PayloadDto(string partitionKey, string value)
        : base(partitionKey)
    {
        Value = value;
    }

    public Event1PayloadDto() { }
}
