using System.Globalization;
using DotNetDistributedApp.Api.Common;
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
    builder
        .Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration))
        .AddSingleton<IMetricsService, MetricsService>()
        .AddSingleton<IDateTimeProvider, DateTimeProvider>();

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
    builder
        .Services.AddScoped<IEventHandler<SimpleEventPayloadDto>, SimpleEventHandler>()
        .AddScoped<IEventHandler<FailingEventPayloadDto>, FailingEventHandler>();

    builder.AddKafkaProducer<string, BaseEventPayloadDto>(
        "events",
        static producerBuilder =>
        {
            var messageSerializer = new EventJsonSerializer<BaseEventPayloadDto>();
            producerBuilder.SetValueSerializer(messageSerializer);
        }
    );
    builder.Services.AddScoped<IEventsService<BaseEventPayloadDto>, EventsService<BaseEventPayloadDto>>();

    builder.Services.AddSingleton<EventsConsumer>();
    builder.Services.AddHostedService<EventsConsumerHostedService>();

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
