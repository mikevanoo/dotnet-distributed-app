using System.Text.Json.Serialization;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Api.Events;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.Extensions.Caching.Hybrid;
using Serilog;

namespace DotNetDistributedApp.Api;

public static class CoreWebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddCoreServices(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();

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

        builder
            .Services.AddApiDatabaseContext<WeatherDbContext>(builder.Configuration)
            .AddScoped<WeatherService>()
            .AddSingleton<IMetricsService, MetricsService>();

        return builder;
    }

    public static WebApplicationBuilder AddCachingServices(this WebApplicationBuilder builder)
    {
        builder.AddRedisDistributedCache(connectionName: "cache");
        builder.AddRedisOutputCache(connectionName: "cache");
        builder
            .Services.AddOutputCache(options =>
            {
                options.AddPolicy(
                    Constants.CachePolicy.WeatherStationHistoricData,
                    policyBuilder => policyBuilder.SetVaryByRouteValue("stationKey").Expire(TimeSpan.FromSeconds(30))
                );
            })
            .AddHybridCache(options =>
            {
                options.DefaultEntryOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(10),
                    LocalCacheExpiration = TimeSpan.FromSeconds(10),
                };
            });

        return builder;
    }

    public static WebApplicationBuilder AddEventServices(this WebApplicationBuilder builder)
    {
        builder.AddKafkaProducer<string, SimpleEventPayloadDto>(
            "events",
            static producerBuilder =>
            {
                var messageSerializer = new EventJsonSerializer<SimpleEventPayloadDto>();
                producerBuilder.SetValueSerializer(messageSerializer);
            }
        );
        builder.Services.AddScoped<IEventsService<SimpleEventPayloadDto>, EventsService<SimpleEventPayloadDto>>();

        return builder;
    }
}
