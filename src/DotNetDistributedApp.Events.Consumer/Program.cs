using System.Globalization;
using Confluent.Kafka;
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
    builder.AddKafkaConsumer<string, SimpleEventPayloadDto>(
        "events",
        settings =>
        {
            settings.Config.GroupId = "events-consumer";
            // settings.Config.AutoOffsetReset = AutoOffsetReset.Earliest; // process all events, even old ones
        },
        static consumerBuilder =>
        {
            var messageSerializer = new EventJsonSerializer<SimpleEventPayloadDto>();
            consumerBuilder.SetValueDeserializer(messageSerializer);
        }
    );
    builder.Services.AddHostedService<EventsConsumer<SimpleEventPayloadDto>>();

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
