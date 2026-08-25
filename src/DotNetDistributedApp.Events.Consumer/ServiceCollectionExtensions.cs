using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Configuration;
using KafkaFlow.Serializer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Acks = KafkaFlow.Acks;
using AutoOffsetReset = KafkaFlow.AutoOffsetReset;

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
    /// <param name="services">The service collection to add the KafkaFlow services to.</param>
    /// <param name="configuration">Supplies the <c>events</c> connection string.</param>
    /// <param name="configureConsumer">
    /// Applied last, so it can override anything configured here. Production passes nothing. It exists for
    /// <c>DotNetDistributedApp.IntegrationTests</c>, which hosts this pipeline in-process and must give it a
    /// different group id: a second consumer in the <c>events-consumer</c> group would share the single
    /// partition with the real service, so only one of them would see any given message.
    /// </param>
    public static IServiceCollection AddEventsConsumerKafka(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IConsumerConfigurationBuilder>? configureConsumer = null
    ) =>
        services.AddKafkaFlowHostedService(kafka =>
        {
            var kafkaConnectionString = configuration.GetConnectionString(ResourceNames.Events);
            kafka.AddCluster(cluster =>
                cluster
                    .WithBrokers([kafkaConnectionString])
                    .CreateTopicIfNotExists(Topics.Common, 1, 1)
                    .CreateTopicIfNotExists(Topics.CommonDlq, 1, 1)
                    // Deliberately has NO serializer middleware. RetryDeadLetterMiddleware sits outside the
                    // deserializer, so it hands this producer the raw bytes it received; KafkaFlow produces a byte[]
                    // value as-is. A JsonCoreSerializer here would base64 the bytes into a JSON string and overwrite
                    // the Message-Type header with System.Byte[], making every dead letter unreadable.
                    .AddProducer<DlqProducer>(producer =>
                        producer
                            .DefaultTopic(Topics.CommonDlq)
                            .WithAcks(Acks.All) // Wait for all replicas to acknowledge
                            .WithProducerConfig(
                                new ProducerConfig
                                {
                                    EnableIdempotence = true, // Prevent duplicate messages on retry
                                    MessageSendMaxRetries = int.MaxValue, // Rely on timeout instead of retry count
                                    MessageTimeoutMs = 300000, // Max time to attempt delivery (5 minutes)
                                    LingerMs = 5, // Wait 5ms to batch messages together for throughput
                                    BatchSize = 16384, // Max batch size in bytes
                                    CompressionType = CompressionType.Lz4, // Compress batches to save bandwidth
                                }
                            )
                    )
                    .AddConsumer(consumer =>
                    {
                        consumer
                            .Topic(Topics.Common)
                            .WithGroupId(ResourceNames.EventsConsumer)
                            .WithBufferSize(100) // In-memory buffer size per worker
                            .WithWorkersCount(3) // Number of concurrent processing threads
                            .WithAutoOffsetReset(AutoOffsetReset.Earliest) // Start from beginning if no offset exists
                            // EnableAutoCommit, EnableAutoOffsetStore and AutoCommitIntervalMs are deliberately absent:
                            // ConsumerConfigurationBuilder.Build overwrites all three whatever is set here. KafkaFlow
                            // owns committing - ConsumerContext.Complete() records the offset in KafkaFlow's own
                            // OffsetManager and its OffsetCommitter commits on a timer. WithAutoCommitIntervalMs is the
                            // only knob that moves that window (default 5s), and the window is why a crash redelivers
                            // messages that were already fully processed - the case the deduplication inbox exists for.
                            .WithConsumerConfig(
                                new ConsumerConfig
                                {
                                    MaxPollIntervalMs = 300000, // Max processing time (5 mins) before broker assumes consumer is dead
                                    SessionTimeoutMs = 10000, // Time before broker detects a silent consumer crash
                                }
                            )
                            .AddMiddlewares(middlewares =>
                                middlewares
                                    // Outermost, and deliberately OUTSIDE the deserializer. DeserializerConsumerMiddleware
                                    // does not catch, so with the order reversed a payload that fails to deserialize threw
                                    // straight past retry/DLQ into ConsumerWorker.ProcessMessageAsync, which logs it,
                                    // swallows it, and stores the offset anyway - no retry, no dead letter, message gone.
                                    .Add<RetryDeadLetterMiddleware>()
                                    // StrictMessageTypeResolver rather than KafkaFlow's default: the default returns null
                                    // for a missing or unloadable Message-Type header, and the deserializer answers null by
                                    // returning without calling next - dropping the message with no exception and no log.
                                    .AddDeserializer<JsonCoreDeserializer, StrictMessageTypeResolver>()
                                    // Inside the deserializer because its counters are tagged by event_name, which does
                                    // not exist until the payload is deserialized; inside RetryDeadLetterMiddleware
                                    // because that swallows the exception once it has dead lettered a message, so
                                    // anything outside it would count a poison message as a success. It also owns the
                                    // "not a BaseEventPayloadDto" decision for everything further in.
                                    .Add<ConsumerMetricsMiddleware>()
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
                            );

                        // Last, so an integration test can override anything set above. See the parameter docs.
                        configureConsumer?.Invoke(consumer);
                    })
            );
        });
}
