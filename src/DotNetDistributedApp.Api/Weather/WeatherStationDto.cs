namespace DotNetDistributedApp.Api.Weather;

public record WeatherStationDto
{
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public decimal Longitude { get; set; }
    public decimal Latitude { get; set; }
}

public record WeatherStationHistoricDataDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal? MeanDailyMaxTemperature { get; set; }
    public decimal? MeanDailyMinTemperature { get; set; }
    public int? DaysOfAirFrost { get; set; }
    public decimal? TotalRainfallMillimeters { get; set; }
    public decimal? TotalSunshineHours { get; set; }
    public bool IsProvisional { get; set; }
}
