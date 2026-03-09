using DotNetDistributedApp.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api.Weather;

public static class WeatherWebApplicationExtensions
{
    public static WebApplication MapWeatherEndpoints(this WebApplication webApplication)
    {
        var weatherGroup = webApplication.MapGroup("/weather");
        weatherGroup.MapGet(
            "/stations",
            async ([FromServices] WeatherService weatherService, CancellationToken cancellationToken) =>
                (await weatherService.GetWeatherStations(cancellationToken)).ToApiResponse()
        );
        weatherGroup
            .MapGet(
                "/stations/{stationKey}/historic-data",
                async ([FromServices] WeatherService weatherService, string stationKey, CancellationToken cancellationToken) =>
                    (await weatherService.GetWeatherStationHistoricData(stationKey, cancellationToken)).ToApiResponse()
            )
            .CacheOutput(Constants.CachePolicy.WeatherStationHistoricData);

        return webApplication;
    }
}
