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
            async ([FromServices] WeatherService weatherService) =>
                (await weatherService.GetWeatherStations()).ToApiResponse()
        );
        weatherGroup
            .MapGet(
                "/stations/{stationKey}/historic-data",
                async ([FromServices] WeatherService weatherService, string stationKey) =>
                    (await weatherService.GetWeatherStationHistoricData(stationKey)).ToApiResponse()
            )
            .CacheOutput(Constants.CachePolicy.WeatherStationHistoricData);

        return webApplication;
    }
}
