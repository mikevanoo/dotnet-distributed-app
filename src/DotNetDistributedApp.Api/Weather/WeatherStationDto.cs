namespace DotNetDistributedApp.Api.Weather;

public record WeatherStationDto
{
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
}