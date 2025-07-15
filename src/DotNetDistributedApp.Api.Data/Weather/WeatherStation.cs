namespace DotNetDistributedApp.Api.Data.Weather;

public sealed class WeatherStation
{
    public int Id { get; set; }
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public double Longitude { get; set; }
    public double Latitude { get; set; }
}

public sealed class WeatherStationHistoricData
{
    public int Id { get; set; }
    public int WeatherStationId { get; set; }
    public WeatherStation WeatherStation { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal? MeanDailyMaxTemperature { get; set; }
    public decimal? MeanDailyMinTemperature { get; set; }
    public int? DaysOfAirFrost { get; set; }
    public decimal? TotalRainfallMillimeters { get; set; }
    public decimal? TotalSunshineHours { get; set; }
    public bool IsProvisional { get; set; }
}
