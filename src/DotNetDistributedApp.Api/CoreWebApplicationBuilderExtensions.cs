using System.Net;
using System.Text.Json.Serialization;
using Asp.Versioning;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Serializer;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Timeout;
using Serilog;

namespace DotNetDistributedApp.Api;

public static class CoreWebApplicationBuilderExtensions
{
    public static WebApplicationBuilder AddCoreServices(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults(MetricsService.MeterName);

        builder
            .Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration))
            .ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            })
            .AddProblemDetails()
            // Ordering for versioning and OpenApi is essential: AddApiVersion() then AddApiExplorer() then AddOpenApi()
            .AddApiVersioning(options =>
            {
                // API versioning by URL segment (api/v1/users)
                options.ApiVersionReader = new UrlSegmentApiVersionReader();
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'VVV";
            })
            .AddOpenApi();

        builder.ConfigureSpatialApiHttpClient();

        builder.Services.AddHttpClient<GeoIpClient>(client =>
        {
            client.BaseAddress = new($"http://{ResourceNames.GeoIpApi}");
        });

        builder
            .Services.AddApiDatabaseContext(builder.Configuration)
            .AddScoped<WeatherService>()
            .AddSingleton<IMetricsService, MetricsService>();

        return builder;
    }

    public static WebApplicationBuilder AddCachingServices(this WebApplicationBuilder builder)
    {
        builder.AddRedisDistributedCache(connectionName: ResourceNames.Cache);
        builder.AddRedisOutputCache(connectionName: ResourceNames.Cache);
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
        var kafkaConnectionString = builder.Configuration.GetConnectionString(ResourceNames.Events);

        builder.Services.AddKafka(kafka =>
            kafka
                .UseMicrosoftLog()
                .AddCluster(cluster =>
                    cluster
                        .WithBrokers([kafkaConnectionString])
                        .CreateTopicIfNotExists(Topics.Common, 1, 1)
                        .AddProducer<EventsService>(producer =>
                            producer
                                .DefaultTopic(Topics.Common)
                                .AddMiddlewares(m => m.AddSerializer<JsonCoreSerializer>())
                        )
                )
        );

        builder.Services.AddScoped<IEventsService, EventsService>();

        return builder;
    }

    private static void ConfigureSpatialApiHttpClient(this WebApplicationBuilder builder)
    {
        var spatialApiClient = builder.Services.AddHttpClient<CoordinateConverterClient>(client =>
        {
            client.BaseAddress = new($"https://{ResourceNames.SpatialApi}");
        });

#pragma warning disable EXTEXP0001
        spatialApiClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        spatialApiClient.AddResilienceHandler(
            "spatial-fallback",
            pipeline =>
                pipeline.AddFallback(
                    new FallbackStrategyOptions<HttpResponseMessage>
                    {
                        ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                            .Handle<HttpRequestException>()
                            .Handle<BrokenCircuitException>()
                            .Handle<TimeoutRejectedException>(),
                        FallbackAction = _ =>
                            Outcome.FromResultAsValueTask(new HttpResponseMessage(HttpStatusCode.NoContent)),
                    }
                )
        );

        spatialApiClient.AddStandardResilienceHandler(
            builder.Configuration.GetSection("HttpClient:CoordinateConverterClient:Resilience")
        );
    }
}
