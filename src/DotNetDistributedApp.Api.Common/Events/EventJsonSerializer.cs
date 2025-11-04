using System.Text.Json;
using Confluent.Kafka;

namespace DotNetDistributedApp.Api.Common.Events;

public class EventJsonSerializer<T> : ISerializer<T>, IDeserializer<T>
    where T : new()
{
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] Serialize(T data, SerializationContext context) =>
        JsonSerializer.SerializeToUtf8Bytes(data, _options);

    public T Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
        {
            return new T();
        }
        var result = JsonSerializer.Deserialize<T>(data, _options);
        return result ?? new T();
    }
}
