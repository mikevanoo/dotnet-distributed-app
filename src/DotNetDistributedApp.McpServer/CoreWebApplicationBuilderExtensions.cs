using System.Net;
using System.Text.Json.Serialization;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.McpServer.Clients;
using DotNetDistributedApp.McpServer.Tools;
using DotNetDistributedApp.ServiceDefaults;
using ModelContextProtocol.AspNetCore;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Timeout;
using Serilog;

namespace DotNetDistributedApp.McpServer;

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
            .AddProblemDetails();

        builder.ConfigureWeatherApiHttpClient();

        builder.Services.AddSingleton<IMetricsService, MetricsService>();

        return builder;
    }

    public static WebApplicationBuilder AddMcpServerServices(this WebApplicationBuilder builder)
    {
        builder
            .Services.AddMcpServer()
            .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)
            .WithTools<WeatherTools>();

        return builder;
    }

    private static void ConfigureWeatherApiHttpClient(this WebApplicationBuilder builder)
    {
        var weatherApiClient = builder.Services.AddHttpClient<WeatherApiClient>(client =>
        {
            // See the note on the spatial-api client: "https+http" is required so this resolves in
            // Kubernetes, where only an http endpoint is registered for the API.
            client.BaseAddress = new($"https+http://{ResourceNames.Api}");
        });

#pragma warning disable EXTEXP0001
        weatherApiClient.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        weatherApiClient.AddResilienceHandler(
            "api-fallback",
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

        weatherApiClient.AddStandardResilienceHandler(
            builder.Configuration.GetSection("HttpClient:WeatherApiClient:Resilience")
        );
    }
}
