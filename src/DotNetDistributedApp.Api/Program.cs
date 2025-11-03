using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using DotNetDistributedApp.Api;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Events;
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
    builder.Services.AddSingleton<IMetricsService, MetricsService>();

    builder.AddKafkaProducer<string, Event1PayloadDto>(
        "events",
        static producerBuilder =>
        {
            var messageSerializer = new EventJsonSerializer<Event1PayloadDto>();
            producerBuilder.SetValueSerializer(messageSerializer);
        }
    );
    builder.Services.AddScoped<IEventsService<Event1PayloadDto>, EventsService<Event1PayloadDto>>();

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

    var eventsGroup = app.MapGroup("/events");
    eventsGroup.MapPost(
        "/event1",
        async (
            [FromBody] [Required] Event1Request body,
            [FromServices] IEventsService<Event1PayloadDto> eventsService
        ) => await eventsService.SendEvent(Topics.Common, new Event1PayloadDto(Guid.NewGuid().ToString(), body.Value))
    );

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
