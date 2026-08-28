using System.ComponentModel;
using DotNetDistributedApp.McpServer.Clients;

namespace DotNetDistributedApp.McpServer.Tools;

public record WeatherStationsDto
{
    [Description("The UK weather stations, each with the stationKey required by the other weather tools.")]
    public required IReadOnlyList<WeatherStationDto> Stations { get; init; }
}

public record WeatherStationHistoricDataDto
{
    [Description("The historic data for a single weather station.")]
    public required IReadOnlyList<Clients.WeatherStationHistoricDataDto> StationHistoricData { get; init; }
}

public record SummarisedWeatherStationHistoricDataDto
{
    [Description(
        "Earliest year present in the data. This can be later than the requested fromYear when the station has"
            + " no readings that far back. Null when nothing matched."
    )]
    public int? FromYear { get; init; }

    [Description(
        "Latest year present in the data. This can be earlier than the requested toYear. Null when nothing matched."
    )]
    public int? ToYear { get; init; }

    [Description(
        "Mean daily maximum temperature in degrees Celsius, weighted by the number of days in each month."
            + " Null when no month has a reading."
    )]
    public double? MeanDailyMaxTemperature { get; init; }

    [Description(
        "Mean daily minimum temperature in degrees Celsius, weighted by the number of days in each month."
            + " Null when no month has a reading."
    )]
    public double? MeanDailyMinTemperature { get; init; }

    [Description(
        "Total number of days of air frost across every month that has a reading."
            + " Null when no month has a reading."
    )]
    public int? DaysOfAirFrost { get; init; }

    [Description(
        "Total rainfall in millimetres across every month that has a reading. Null when no month has a reading."
    )]
    public double? TotalRainfallMillimeters { get; init; }

    [Description("Total sunshine hours across every month that has a reading. Null when no month has a reading.")]
    public double? TotalSunshineHours { get; init; }

    [Description(
        "How complete the underlying data is. Every figure above covers only the months that have a reading,"
            + " so check this before reporting a total as complete."
    )]
    public required HistoricDataCoverageDto Coverage { get; init; }
}

public record HistoricDataCoverageDto
{
    [Description("Monthly readings expected for the years present: twelve for each year from fromYear to toYear.")]
    public int MonthsExpected { get; init; }

    [Description("Monthly readings actually returned for those years.")]
    public int MonthsReturned { get; init; }

    [Description("Months contributing to the mean daily maximum temperature.")]
    public int MeanDailyMaxTemperatureMonths { get; init; }

    [Description("Months contributing to the mean daily minimum temperature.")]
    public int MeanDailyMinTemperatureMonths { get; init; }

    [Description("Months contributing to the total days of air frost.")]
    public int DaysOfAirFrostMonths { get; init; }

    [Description("Months contributing to the total rainfall.")]
    public int TotalRainfallMonths { get; init; }

    [Description("Months contributing to the total sunshine hours.")]
    public int TotalSunshineMonths { get; init; }

    [Description("Months whose readings are still provisional and may be revised.")]
    public int ProvisionalMonths { get; init; }
}
