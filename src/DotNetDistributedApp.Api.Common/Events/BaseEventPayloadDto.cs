namespace DotNetDistributedApp.Api.Common.Events;

public abstract class BaseEventPayloadDto(string partitionKey)
{
    public Guid EventId { get; set; } = Guid.NewGuid();
    public abstract string EventName { get; }
    public string PartitionKey { get; set; } = partitionKey;

    protected BaseEventPayloadDto()
        : this(string.Empty) { }
}
