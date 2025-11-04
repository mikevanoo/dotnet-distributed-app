namespace DotNetDistributedApp.Api.Common.Events;

public abstract class BaseEventPayloadDto
{
    public abstract string EventName { get; }
    public string PartitionKey { get; set; }

    protected BaseEventPayloadDto(string partitionKey)
    {
        PartitionKey = partitionKey;
    }

    protected BaseEventPayloadDto()
    {
        PartitionKey = string.Empty;
    }
}
