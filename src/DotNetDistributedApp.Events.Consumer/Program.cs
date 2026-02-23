using System.Globalization;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Events.Consumer;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.AddServiceDefaults();
    builder.Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration));
    builder.Services.AddSingleton<IMetricsService, MetricsService>();

    builder.AddKafkaConsumer<string, BaseEventPayloadDto>(
        "events",
        settings =>
        {
            settings.Config.GroupId = "events-consumer";
            // settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest; // process all events, even old ones
        },
        static consumerBuilder =>
        {
            var deserializer = new EventJsonSerializer<BaseEventPayloadDto>();
            consumerBuilder.SetValueDeserializer(deserializer);
        }
    );
    builder.Services.AddScoped<IEventHandler<SimpleEventPayloadDto>, SimpleEventHandler>();
    builder.Services.AddScoped<IEventHandler<FailingEventPayloadDto>, FailingEventHandler>();
    builder.Services.AddHostedService<EventsConsumer>();

    var app = builder.Build();
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
