namespace DotNetDistributedApp.Api.Weather;

public record WeatherStationDto
{
    public required string Key { get; set; }
    public required string DisplayName { get; set; }
    public double Longitude { get; set; }
    public double Latitude { get; set; }
    public double? Easting { get; set; }
    public double? Northing { get; set; }
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
