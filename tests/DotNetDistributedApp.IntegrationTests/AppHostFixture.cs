using System.Diagnostics.Metrics;
using Aspire.Hosting;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Events.Consumer;
using DotNetDistributedApp.IntegrationTests;
using DotNetDistributedApp.IntegrationTests.Api.Events;
using DotNetDistributedApp.IntegrationTests.EventsConsumer;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Serializer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace DotNetDistributedApp.IntegrationTests;

/*
 * Assembly-level fixture: one AppHost, shared by every test class, started once.
 *
 * It holds TWO service providers, each with its own IKafkaBus, and they are not interchangeable:
 *
 *   #1  ConfigureKafkaServices        - producer + a minimal mock-handler consumer, for producer tests, plus a
 *                                       consumer on common-dlq for tests that need to know an event was dead lettered.
 *                                       Reached via GetMessageProducer<T>() / GetMessageHandler<T>() /
 *                                       WaitForDeadLetteredEvent().
 *   #2  ConfigureEventsConsumerServices - the real AddEventsConsumerKafka pipeline hosted in-process, for
 *                                       tests that need the deduplication inbox or the consumer's metrics.
 *                                       Reached via EventsConsumerServices / CreateEventsConsumerScope().
 *
 * Two containers rather than one is a constraint: CreateKafkaBus resolves a SINGLE KafkaFlowConfigurator, so a
 * second AddKafka call in the same container would make the first cluster silently unreachable.
 *
 * What keeps the two buses (and the out-of-process events-consumer service) from stealing each other's
 * messages is not DI - all three read the same `common` topic. It is the consumer group id: Kafka gives every
 * group its own copy of every message.
 *
 * >> Scope every processed_weather_events assertion to EventsConsumerGroupId. Postgres has a data volume so
 * >> rows outlive the run, and the real events-consumer writes its own rows throughout. This is the one rule
 * >> you can break and still get a passing test today.
 *
 * Full explanation, including the DLQ producer gotcha and how to write a new events-consumer test: README.md
 * in this project.
 */
public class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public DistributedApplication App { get; private set; } = null!;
    private ServiceProvider _kafkaServiceProvider = null!;
    private IKafkaBus _kafkaBus = null!;
    private ServiceProvider _eventsConsumerServiceProvider = null!;
    private IKafkaBus _eventsConsumerKafkaBus = null!;

    /// <summary>
    /// The consumer group used by the in-process events consumer pipeline, unique per run. Filter every
    /// <c>processed_weather_events</c> assertion by this.
    /// </summary>
    public string EventsConsumerGroupId { get; } = $"integration-tests-events-consumer-{Guid.NewGuid()}";

    /// <summary>
    /// The service provider hosting the real events consumer pipeline, for tests that need to reach past the
    /// pipeline into its dependencies — <see cref="IMeterFactory"/> to observe the consumer's metrics, or
    /// <see cref="WeatherDbContext"/> (via <see cref="CreateEventsConsumerScope"/>) to assert on the inbox table.
    /// </summary>
    public IServiceProvider EventsConsumerServices => _eventsConsumerServiceProvider;

    /// <summary>
    /// Creates a DI scope over <see cref="EventsConsumerServices"/>. <see cref="WeatherDbContext"/> is scoped
    /// and pooled, so it must be resolved from a scope rather than the root provider.
    /// </summary>
    public AsyncServiceScope CreateEventsConsumerScope() => _eventsConsumerServiceProvider.CreateAsyncScope();

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = CreateCancellationToken();
        var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.DotNetDistributedApp_AppHost>(
            cancellationToken
        );
        appHost.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            // Override the logging filters from the app's configuration
            logging.AddFilter(appHost.Environment.ApplicationName, LogLevel.Debug);
            logging.AddFilter("Aspire.", LogLevel.Debug);
            // To output logs to the xUnit.net ITestOutputHelper, consider adding a package from https://www.nuget.org/packages?q=xunit+logging
        });
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });

        App = await appHost.BuildAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);
        await App.StartAsync(cancellationToken).WaitAsync(DefaultTimeout, cancellationToken);

        await App
            .ResourceNotifications.WaitForResourceHealthyAsync(ResourceNames.Api, cancellationToken)
            .WaitAsync(DefaultTimeout, cancellationToken);

        await ConfigureKafkaServices(cancellationToken);
        await ConfigureEventsConsumerServices(cancellationToken);
    }

    public static CancellationToken CreateCancellationToken(TimeSpan? timeout = null) =>
        new CancellationTokenSource(timeout ?? DefaultTimeout).Token;

    public async ValueTask DisposeAsync()
    {
        await _eventsConsumerKafkaBus.StopAsync();
        await PurgeProcessedWeatherEvents();
        await _eventsConsumerServiceProvider.DisposeAsync();
        await _kafkaBus.StopAsync();
        await _kafkaServiceProvider.DisposeAsync();
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /*
     * Stops the table growing a run's worth of rows every time the suite executes (Postgres has a data volume).
     *
     * Not a before-each clear: test classes run in parallel, so a broader delete would remove rows another class
     * is still waiting on, and would strip the real events-consumer service of records it needs. At dispose the
     * in-process consumer is already stopped, so nothing can be writing under this group id.
     */
    private async ValueTask PurgeProcessedWeatherEvents()
    {
        await using var scope = CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();

        await dbContext
            .ProcessedWeatherEvents.Where(x => x.ConsumerGroup == EventsConsumerGroupId)
            .ExecuteDeleteAsync(CreateCancellationToken());
    }

    private async ValueTask ConfigureKafkaServices(CancellationToken cancellationToken)
    {
        // The real connection string is only known after the Kafka container has started.
        var kafkaConnectionString = await App.GetConnectionStringAsync(ResourceNames.Events, cancellationToken);
        var kafkaServices = new ServiceCollection();

        kafkaServices
            .AddLogging()
            .AddSingleton(Substitute.For<IMessageHandler<TestMessage>>())
            .AddSingleton<DeadLetterRecorder>()
            .AddKafka(kafka =>
                kafka
                    .UseMicrosoftLog()
                    .AddCluster(cluster =>
                        cluster
                            .WithBrokers([kafkaConnectionString])
                            .CreateTopicIfNotExists(Topics.Common, 1, 1)
                            .CreateTopicIfNotExists(Topics.CommonDlq, 1, 1)
                            .AddProducer<EventsService>(producer =>
                                producer
                                    .DefaultTopic(Topics.Common)
                                    .AddMiddlewares(m => m.AddSerializer<JsonCoreSerializer>())
                            )
                            /*
                             * Observes the dead letter topic so a test can await an event reaching it. Nothing else
                             * consumes common-dlq, and the events-consumer pipeline must not: it would deserialize its
                             * own dead letters back off the topic and fail them all over again.
                             *
                             * It is a separate consumer rather than another handler on the one below because typed
                             * handlers are configured per consumer. Keeping them apart is what makes the barrier mean
                             * something - a handler that fired on the original common message would prove nothing.
                             */
                            .AddConsumer(consumer =>
                                consumer
                                    .Topic(Topics.CommonDlq)
                                    .WithGroupId($"integration-tests-dead-letter-{Guid.NewGuid()}")
                                    .WithAutoOffsetReset(AutoOffsetReset.Earliest)
                                    .WithBufferSize(5)
                                    .WithWorkersCount(1)
                                    .AddMiddlewares(middlewares =>
                                        middlewares
                                            .AddDeserializer<JsonCoreDeserializer>()
                                            .AddTypedHandlers(x =>
                                                x.AddHandler<DeadLetteredFailingEventMessageHandler>()
                                            )
                                    )
                            )
                            .AddConsumer(consumer =>
                                consumer
                                    .Topic(Topics.Common)
                                    .WithGroupId($"integration-tests-{Guid.NewGuid()}")
                                    /*
                                     * KafkaFlow's StartAsync launches the consumer in the background but returns before
                                     * partition assignment completes. With the default latest offset reset, the consumer
                                     * determines its starting position AFTER it first polls the broker. If SendEvent runs
                                     * while partition assignment is still in progress, the message lands at offset N,
                                     * and when the consumer finally polls for the first time it sets "start from latest" = N+1
                                     * thus skipping the message entirely. Using earliest removes the race: the consumer
                                     * always starts from offset 0, so it catches the message regardless of when it was
                                     * produced relative to when the consumer subscribed.
                                     */
                                    .WithAutoOffsetReset(AutoOffsetReset.Earliest)
                                    .WithBufferSize(5)
                                    .WithWorkersCount(3)
                                    .AddMiddlewares(middlewares =>
                                        middlewares
                                            .AddDeserializer<JsonCoreDeserializer>()
                                            .AddTypedHandlers(x => x.AddHandler<DelegatingTestMessageHandler>())
                                    )
                            )
                    )
            );
        _kafkaServiceProvider = kafkaServices.BuildServiceProvider();
        _kafkaBus = _kafkaServiceProvider.CreateKafkaBus();
        await _kafkaBus.StartAsync(cancellationToken);
    }

    // Hosts the real AddEventsConsumerKafka pipeline in-process. The service registrations below mirror
    // Events.Consumer/Program.cs - if the pipeline gains a dependency there, it needs one here too.
    private async ValueTask ConfigureEventsConsumerServices(CancellationToken cancellationToken)
    {
        // The real connection strings are only known after the Kafka and Postgres containers have started.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"ConnectionStrings:{ResourceNames.Events}"] = await App.GetConnectionStringAsync(
                    ResourceNames.Events,
                    cancellationToken
                ),
                [$"ConnectionStrings:{ResourceNames.ApiDatabase}"] = await App.GetConnectionStringAsync(
                    ResourceNames.ApiDatabase,
                    cancellationToken
                ),
                // Production backs off 2s/4s/8s before dead-lettering, which is longer than a test wants to wait.
                ["RetryDeadLetter:MaxRetryCount"] = "1",
                ["RetryDeadLetter:RetryDelay"] = "00:00:00.200",
                ["RetryDeadLetter:UseExponentialBackoff"] = "false",
            }
        );

        var services = new ServiceCollection();
        services
            .AddLogging()
            .AddMetrics()
            .AddApiDatabaseContext(configuration)
            .AddSingleton<IMetricsService, MetricsService>()
            .Configure<RetryDeadLetterOptions>(configuration.GetSection("RetryDeadLetter"))
            .AddEventsConsumerKafka(
                configuration,
                consumer => consumer.WithGroupId(EventsConsumerGroupId).WithAutoOffsetReset(AutoOffsetReset.Earliest)
            );

        // Scope validation matches what Host.CreateApplicationBuilder turns on in Development, and is what fails
        // loudly if a handler is ever registered singleton and captures a root WeatherDbContext instead of the
        // middleware's scoped one. Without it this fixture would reproduce the silent Production failure mode.
        _eventsConsumerServiceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }
        );
        // AddKafkaFlowHostedService only registers an IHostedService that would call this; there is no Host here.
        _eventsConsumerKafkaBus = _eventsConsumerServiceProvider.CreateKafkaBus();
        await _eventsConsumerKafkaBus.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Completes once the in-process events consumer pipeline has dead lettered <paramref name="eventId"/>, meaning it
    /// exhausted its retries and gave up. Use it as a barrier before asserting that a failed event left no trace:
    /// <see cref="WeatherDeduplicationMiddleware"/>'s transaction is disposed — and so rolled back — as the handler's
    /// exception unwinds, strictly before <see cref="RetryDeadLetterMiddleware"/> catches it and produces to
    /// <c>common-dlq</c>. Once the dead letter arrives, the inbox is in its final state for that event.
    /// </summary>
    /// <remarks>
    /// The recorder lives in provider #1 (it is a consumer of <c>common-dlq</c>) but filters on provider #2's group id.
    /// The <c>events-consumer</c> service dead letters the same event under its own group id, and can beat the
    /// in-process pipeline to it — waiting on that one would put the barrier back before the rollback it is meant to
    /// prove has happened.
    /// </remarks>
    public async Task WaitForDeadLetteredEvent(Guid eventId, CancellationToken cancellationToken)
    {
        try
        {
            await _kafkaServiceProvider
                .GetRequiredService<DeadLetterRecorder>()
                .WaitFor(EventsConsumerGroupId, eventId, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                $"""
                Timed out waiting for event {eventId} to reach {Topics.CommonDlq} under consumer group
                {EventsConsumerGroupId}.

                Either the in-process pipeline never received the event, or it stopped failing before its retries ran
                out - which is itself a failure worth reading carefully. If WeatherDeduplicationMiddleware records the
                inbox row despite the handler throwing, the retry finds the event already processed, skips the handler,
                and succeeds. The event is then never dead lettered, so this barrier times out instead of the emptiness
                assertion failing.
                """,
                exception
            );
        }
    }

    public IMessageProducer<T> GetMessageProducer<T>() =>
        _kafkaServiceProvider.GetRequiredService<IMessageProducer<T>>();

    public IMessageHandler<T> GetMessageHandler<T>() => _kafkaServiceProvider.GetRequiredService<IMessageHandler<T>>();
}
