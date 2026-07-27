namespace DotNetDistributedApp.Api.Data.Weather;

public class ProcessedWeatherEvent
{
    public Guid Id { get; set; }
    public string EventName { get; set; }
    public string Topic { get; set; }
    public string PartitionKey { get; set; }
    public string ConsumerGroup { get; set; }
    public int Partition { get; set; }
    public long Offset { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}
