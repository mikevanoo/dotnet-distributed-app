namespace DotNetDistributedApp.Api.Data.Weather;

public sealed class WeatherStation
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
}