using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Serializer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetDistributedApp.Events.Consumer;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the KafkaFlow cluster, producers and consumers for this service.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>Program.cs</c> so the registration itself can be asserted on by
    /// <c>EventsConsumerRegistrationShould</c> — the guard tests only have value if they exercise the
    /// real configuration rather than a copy of it.
    /// </remarks>
    public static IServiceCollection AddEventsConsumerKafka(
        this IServiceCollection services,
        IConfiguration configuration
    ) =>
        services.AddKafkaFlowHostedService(kafka =>
        {
            var kafkaConnectionString = configuration.GetConnectionString(ResourceNames.Events);
            kafka.AddCluster(cluster =>
                cluster
                    .WithBrokers([kafkaConnectionString])
                    .CreateTopicIfNotExists(Topics.Common, 1, 1)
                    .CreateTopicIfNotExists(Topics.CommonDlq, 1, 1)
                    .AddProducer<DlqProducer>(producer =>
                        producer
                            .DefaultTopic(Topics.CommonDlq)
                            .AddMiddlewares(m => m.AddSerializer<JsonCoreSerializer>())
                    )
                    .AddConsumer(consumer =>
                        consumer
                            .Topic(Topics.Common)
                            .WithGroupId(ResourceNames.EventsConsumer)
                            .WithBufferSize(5)
                            .WithWorkersCount(3)
                            .AddMiddlewares(middlewares =>
                                middlewares
                                    .AddDeserializer<JsonCoreDeserializer>()
                                    .Add<RetryDeadLetterMiddleware>()
                                    // MiddlewareLifetime.Message is required so that each worker gets their own WeatherDbContext (thread-safety)
                                    // and this also lets the middleware and handlers share the middlewares DB transaction
                                    .Add<WeatherDeduplicationMiddleware>(MiddlewareLifetime.Message)
                                    // InstanceLifetime.Scoped is required, and is NOT the KafkaFlow default (Singleton).
                                    // Handlers must resolve from the same per-message DI scope as
                                    // WeatherDeduplicationMiddleware, otherwise they get a different WeatherDbContext
                                    // and their writes fall outside the middleware's transaction.
                                    // It must be set on every AddTypedHandlers call - the setting does not carry across calls.
                                    .AddTypedHandlers(x =>
                                        x.WithHandlerLifetime(InstanceLifetime.Scoped)
                                            .AddHandler<SimpleEventMessageHandler>()
                                    )
                                    .AddTypedHandlers(x =>
                                        x.WithHandlerLifetime(InstanceLifetime.Scoped)
                                            .AddHandler<FailingEventMessageHandler>()
                                    )
                            )
                    )
            );
        });
}
