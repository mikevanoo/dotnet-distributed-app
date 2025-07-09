namespace DotNetDistributedApp.Api.Weather;

public record WeatherStationDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
}