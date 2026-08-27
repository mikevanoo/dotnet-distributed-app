using System.ComponentModel;
using DotNetDistributedApp.McpServer.Clients;

namespace DotNetDistributedApp.McpServer.Tools;

public record WeatherStationsDto
{
    [Description("The UK weather stations, each with the stationKey required by the other weather tools.")]
    public required IReadOnlyList<WeatherStationDto> Stations { get; init; }
}
