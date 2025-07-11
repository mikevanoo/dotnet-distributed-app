namespace DotNetDistributedApp.Api.Data.Weather;

public sealed class WeatherStation
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
}