namespace DotNetDistributedApp.Api.Common.Events;

public class SimpleEventPayloadDto : BaseEventPayloadDto
{
    public override string EventName => "simple-event";

    public string? Value { get; set; }

    public SimpleEventPayloadDto(string partitionKey, string value)
        : base(partitionKey)
    {
        Value = value;
    }

    public SimpleEventPayloadDto() { }
}
