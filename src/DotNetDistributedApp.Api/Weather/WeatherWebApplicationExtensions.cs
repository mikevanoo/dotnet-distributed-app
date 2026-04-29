using DotNetDistributedApp.Api.Common;
using Microsoft.AspNetCore.Mvc;

namespace DotNetDistributedApp.Api.Weather;

public static class WeatherWebApplicationExtensions
{
    public static WebApplication MapWeatherEndpoints(this WebApplication webApplication)
    {
        var api = webApplication.NewVersionedApi("Weather");

        var v1v2 = api.MapGroup("/v{version:apiVersion}/weather").HasApiVersion(1.0).HasApiVersion(2.0);
        var v2 = api.MapGroup("/v{version:apiVersion}/weather").HasApiVersion(2.0);

        v1v2.MapGet(
            "/stations",
            async ([FromServices] WeatherService weatherService, CancellationToken cancellationToken) =>
                (await weatherService.GetWeatherStations(cancellationToken)).ToApiResponse()
        );
        v1v2.MapGet(
                "/stations/{stationKey}/historic-data",
                async (
                    [FromServices] WeatherService weatherService,
                    string stationKey,
                    CancellationToken cancellationToken
                ) => (await weatherService.GetWeatherStationHistoricData(stationKey, cancellationToken)).ToApiResponse()
            )
            .CacheOutput(Constants.CachePolicy.WeatherStationHistoricData);

        v2.MapGet("/more-stations", () => new { message = "More stations coming soon" });

        return webApplication;
    }
}
