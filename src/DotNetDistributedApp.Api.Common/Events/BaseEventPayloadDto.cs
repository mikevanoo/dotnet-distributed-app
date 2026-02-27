namespace DotNetDistributedApp.Api.Common.Events;

public abstract class BaseEventPayloadDto(string partitionKey)
{
    public static readonly Dictionary<string, Type> EventPayloadMap = new()
    {
        ["simple-event"] = typeof(SimpleEventPayloadDto),
        ["failing-event"] = typeof(FailingEventPayloadDto),
    };

    public abstract string EventName { get; }
    public string PartitionKey { get; set; } = partitionKey;

    protected BaseEventPayloadDto()
        : this(string.Empty) { }
}
