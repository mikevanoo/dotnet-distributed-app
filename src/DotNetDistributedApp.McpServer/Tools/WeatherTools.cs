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
}
