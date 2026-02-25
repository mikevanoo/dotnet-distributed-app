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
    public RetryMetadata Retry { get; set; } = new();

    public class RetryMetadata
    {
        public string? TargetTopic { get; set; }
        public int FailedCount { get; set; }
    }

    protected BaseEventPayloadDto()
        : this(string.Empty) { }
}
