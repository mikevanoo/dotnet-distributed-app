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
