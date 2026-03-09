using System.Collections.Frozen;

namespace DotNetDistributedApp.Api.Common.Events;

public abstract class BaseEventPayloadDto(string partitionKey)
{
    public static readonly FrozenDictionary<string, Type> EventPayloadMap = (
        new Dictionary<string, Type>
        {
            ["simple-event"] = typeof(SimpleEventPayloadDto),
            ["failing-event"] = typeof(FailingEventPayloadDto),
        }
    ).ToFrozenDictionary();

    public abstract string EventName { get; }
    public string PartitionKey { get; set; } = partitionKey;

    protected BaseEventPayloadDto()
        : this(string.Empty) { }
}
