using System.Globalization;
using System.Net;
using FluentResults;

namespace DotNetDistributedApp.McpServer.Clients;

public partial class WeatherApiClient(HttpClient httpClient, ILogger<WeatherApiClient> logger)
{
    public async Task<IReadOnlyList<WeatherStationDto>> GetWeatherStations(CancellationToken cancellationToken)
    {
        var response = await httpClient.GetFromJsonAsync<ResponseDtoOfListOfWeatherStationDto>(
            "/v2.0/weather/stations",
            cancellationToken
        );
        return response?.Response?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<WeatherStationHistoricDataDto>> GetWeatherStationHistoricData(
        string stationKey,
        CancellationToken cancellationToken
    )
    {
        var response = await httpClient.GetFromJsonAsync<ResponseDtoOfListOfWeatherStationHistoricDataDto>(
            $"/v2.0/weather/stations/{stationKey}/historic-data",
            cancellationToken
        );
        return response?.Response?.ToArray() ?? [];
    }
}
