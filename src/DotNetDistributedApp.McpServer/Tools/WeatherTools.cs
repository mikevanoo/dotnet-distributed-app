using System.ComponentModel;
using DotNetDistributedApp.McpServer.Clients;
using FluentResults;
using ModelContextProtocol.Server;

namespace DotNetDistributedApp.McpServer.Tools;

[McpServerToolType]
public class WeatherTools(WeatherApiClient weatherApiClient)
{
    private const string StationKeyHint = "Call list_weather_stations to get the station keys this server accepts.";

    [McpServerTool(Name = "list_weather_stations", UseStructuredContent = true, ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists all UK weather stations with their name and location. Call this first to resolve a place name such"
            + " as 'Heathrow' to the stationKey required by the other weather tools."
    )]
    public async Task<WeatherStationsDto> ListWeatherStations(CancellationToken cancellationToken) =>
        new() { Stations = (await weatherApiClient.GetWeatherStations(cancellationToken)).ValueOrToolError() };

    [McpServerTool(Name = "get_station_historic_data", UseStructuredContent = true, ReadOnly = true, Idempotent = true)]
    [Description(
        "Returns monthly historic weather readings for a single station. "
            + "Each row covers one calendar month. Use the optional filters to narrow the result: "
            + "an unfiltered station can return over a century of monthly rows."
    )]
    public async Task<WeatherStationHistoricDataDto> GetWeatherStationHistoricData(
        [Description("The station key, from list_weather_stations.")] string stationKey,
        [Description("Earliest year to include, inclusive. Omit for no lower bound.")] int? fromYear,
        [Description("Latest year to include, inclusive. Omit for no upper bound.")] int? toYear,
        CancellationToken cancellationToken
    )
    {
        var historicData = await GetHistoricData(stationKey, fromYear, toYear, cancellationToken);

        return new() { StationHistoricData = historicData.ValueOrToolError(StationKeyHint) };
    }

    [McpServerTool(
        Name = "summarise_station_historic_data",
        UseStructuredContent = true,
        ReadOnly = true,
        Idempotent = true
    )]
    [Description(
        "Returns aggregated historic weather data for a station over a range of years: mean daily maximum and "
            + "minimum temperature weighted by the length of each month, plus total rainfall, total sunshine hours "
            + "and total days of air frost. Each figure covers only the months that carry a reading, and the "
            + "coverage object reports how many that was, so check it before describing a total as complete. "
            + "Prefer this over get_station_historic_data whenever the question asks for an average, "
            + "total or comparison rather than for individual monthly readings."
    )]
    public async Task<SummarisedWeatherStationHistoricDataDto> SummariseWeatherStationHistoricData(
        [Description("The station key, from list_weather_stations.")] string stationKey,
        [Description("Earliest year to include, inclusive.")] int fromYear,
        [Description("Latest year to include, inclusive.")] int toYear,
        CancellationToken cancellationToken
    )
    {
        var historicData = await GetHistoricData(stationKey, fromYear, toYear, cancellationToken);

        return WeatherStationHistoricDataSummariser.Summarise(historicData.ValueOrToolError(StationKeyHint));
    }

    private async Task<Result<IReadOnlyList<Clients.WeatherStationHistoricDataDto>>> GetHistoricData(
        string stationKey,
        int? fromYear,
        int? toYear,
        CancellationToken cancellationToken
    )
    {
        // Checked here as well as by the weather API so that the message names the tool's own parameters and the
        // values passed to them, and so an unanswerable request costs no round trip. Comparing nullable ints is
        // false when either side is null, which leaves an open-ended range alone.
        if (fromYear > toYear)
        {
            return Result.Fail(
                new Error(
                    $"fromYear ({fromYear}) is later than toYear ({toYear}), so the range is empty. "
                        + "Pass the earlier year as fromYear and the later year as toYear."
                )
            );
        }

        return await weatherApiClient.GetWeatherStationHistoricData(stationKey, fromYear, toYear, cancellationToken);
    }
}
