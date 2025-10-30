using System.Text.Json;
using Confluent.Kafka;

namespace DotNetDistributedApp.Api.Events;

public class EventJsonSerializer<T> : ISerializer<T>
{
    private readonly JsonSerializerOptions _options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public byte[] Serialize(T data, SerializationContext context) =>
        JsonSerializer.SerializeToUtf8Bytes(data, _options);
}
