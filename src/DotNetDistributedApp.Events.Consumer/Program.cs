using System.Globalization;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Events.Consumer;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Serializer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Common = DotNetDistributedApp.Api.Common;

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
        .AddSingleton<Common.IDateTimeProvider, Common.DateTimeProvider>()
        .AddSingleton<IEventsService, EventsService>()
        .AddKafkaFlowHostedService(kafka =>
        {
            var kafkaConnectionString = builder.Configuration.GetConnectionString("events");
            kafka.AddCluster(cluster =>
                cluster
                    .WithBrokers([kafkaConnectionString])
                    .CreateTopicIfNotExists(Topics.Common, 1, 1)
                    .AddConsumer(consumer =>
                        consumer
                            .Topic(Topics.Common)
                            .WithGroupId("events-consumer")
                            .WithBufferSize(5)
                            .WithWorkersCount(3)
                            .AddMiddlewares(middlewares =>
                                middlewares
                                    .AddDeserializer<JsonCoreDeserializer>()
                                    .AddTypedHandlers(x => x.AddHandler<SimpleEventMessageHandler>())
                                    .AddTypedHandlers(x => x.AddHandler<FailingEventMessageHandler>())
                            )
                    )
            );
        });

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
