using System.Globalization;
using System.Text.Json.Serialization;
using DotNetDistributedApp.Api;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Add service defaults & Aspire client integrations.
    builder.AddServiceDefaults();

    // Add services to the container.
    builder
        .Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration))
        .ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        })
        .AddProblemDetails()
        .AddOpenApi();

    builder.Services.AddHttpClient<CoordinateConverterClient>(client =>
    {
        client.BaseAddress = new("https://spatial-api");
    });
    builder.Services.AddHttpClient<GeoIpClient>(client =>
    {
        client.BaseAddress = new("http://geoip-api");
    });

    builder.Services.AddApiDatabaseContext<WeatherDbContext>(builder.Configuration).AddScoped<WeatherService>();

    builder.AddRedisDistributedCache(connectionName: "cache");
    builder.AddRedisOutputCache(connectionName: "cache");
    builder.Services.AddOutputCache(options =>
    {
        options.AddPolicy(
            Constants.CachePolicy.WeatherStationHistoricData,
            policyBuilder => policyBuilder.SetVaryByRouteValue("stationKey").Expire(TimeSpan.FromSeconds(30))
        );
    });
    builder.Services.AddHybridCache(options =>
    {
        options.DefaultEntryOptions = new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromSeconds(10),
            LocalCacheExpiration = TimeSpan.FromSeconds(10),
        };
    });

    var app = builder.Build();

    // Configure the HTTP request pipeline.
    app.UseExceptionHandler();
    app.UseOutputCache();

    if (app.Environment.IsDevelopment())
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/openapi/v1.json", "v1");
        });
    }

    var weatherGroup = app.MapGroup("/weather");
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

    app.MapDefaultEndpoints();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
