using System.Text.Json;
using Confluent.Kafka;

namespace DotNetDistributedApp.Api.Common.Events;

public class PolymorphicEventDeserializer : IDeserializer<BaseEventPayloadDto>
{
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Dictionary<string, Type> _eventTypeMap = new()
    {
        ["simple-event"] = typeof(SimpleEventPayloadDto),
        ["failing-event"] = typeof(FailingEventPayloadDto),
    };

    public BaseEventPayloadDto Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
        {
            throw new InvalidOperationException("Null event data received");
        }

        // First pass: deserialize to base type to read EventName
        using var doc = JsonDocument.Parse(data.ToArray());
        var root = doc.RootElement;

        if (!root.TryGetProperty("eventName", out var eventNameElement))
        {
            throw new InvalidOperationException("Event data missing 'eventName' property");
        }

        var eventName = eventNameElement.GetString();
        if (string.IsNullOrEmpty(eventName))
        {
            throw new InvalidOperationException("Event name is null or empty");
        }

        // Look up the concrete type
        if (!_eventTypeMap.TryGetValue(eventName, out var concreteType))
        {
            // Fallback: deserialize as base type for unknown events
            return JsonSerializer.Deserialize<BaseEventPayloadDto>(data, _options)
                ?? throw new InvalidOperationException("Failed to deserialize event");
        }

        // Second pass: deserialize to concrete type
        var result = JsonSerializer.Deserialize(data, concreteType, _options) as BaseEventPayloadDto;
        return result ?? throw new InvalidOperationException($"Failed to deserialize event as {concreteType.Name}");
    }
}
