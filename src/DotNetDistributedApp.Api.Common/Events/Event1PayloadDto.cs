namespace DotNetDistributedApp.Api.Common.Events;

public class Event1PayloadDto(string partitionKey, string value) : BaseEventPayloadDto(partitionKey)
{
    public override string EventName => "event1";

    public string Value { get; set; } = value;
}
