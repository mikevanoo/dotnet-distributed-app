namespace DotNetDistributedApp.Api.Common.Events;

public abstract class BaseEventPayloadDto(string partitionKey)
{
    public abstract string EventName { get; }
    public string PartitionKey { get; set; } = partitionKey;

    protected BaseEventPayloadDto()
        : this(string.Empty) { }
}
