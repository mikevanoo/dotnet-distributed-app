using System.ComponentModel;
using DotNetDistributedApp.McpServer.Clients;
using ModelContextProtocol.Server;

namespace DotNetDistributedApp.McpServer.Tools;

[McpServerToolType]
public class WeatherTools(WeatherApiClient weatherApiClient)
{
    [McpServerTool(Name = "list_weather_stations", UseStructuredContent = true, ReadOnly = true, Idempotent = true)]
    [Description(
        "Lists all UK weather stations with their name and location. Call this first to resolve a place name such"
            + " as 'Heathrow' to the stationKey required by the other weather tools."
    )]
    public async Task<WeatherStationsDto> ListWeatherStations(CancellationToken cancellationToken) =>
        new() { Stations = await weatherApiClient.GetWeatherStations(cancellationToken) };

    [McpServerTool(Name = "get_station_historic_data", UseStructuredContent = true)]
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
    ) =>
        new()
        {
            StationHistoricData = await weatherApiClient.GetWeatherStationHistoricData(
                stationKey,
                fromYear,
                toYear,
                cancellationToken
            ),
        };
}
