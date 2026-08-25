using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aspire.Hosting;
using Confluent.Kafka;
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
using Acks = KafkaFlow.Acks;
using AutoOffsetReset = KafkaFlow.AutoOffsetReset;

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

    /*
     * Startup gets a budget per stage instead of one deadline spanning all of them, and the reason is the error
     * message. A single token started before the first stage is usually the thing that trips, wherever the run
     * actually got stuck, and a tripped token surfaces as `TaskCanceledException: A task was canceled.` - no stage,
     * no elapsed time, nothing about which container never came up. RunStartupStage below is what turns each of
     * these into a sentence.
     *
     * The numbers are sized for a cold Docker: the first run of the day pulls the Postgres, Kafka, Valkey and GeoIP
     * images before anything can start. On a healthy run nothing waits on them, so generous costs nothing.
     */
    private static readonly TimeSpan AppHostCreateTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AppHostBuildTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AppHostStartTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ResourceHealthyTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan KafkaBusStartTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How long <see cref="DescribeResourceStates"/> drains the notification stream for. Failure path only.
    /// </summary>
    private static readonly TimeSpan ResourceStateDrainTimeout = TimeSpan.FromSeconds(5);

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
        var appHost = await RunStartupStage(
            "Creating the AppHost app model",
            AppHostCreateTimeout,
            DistributedApplicationTestingBuilder.CreateAsync<Projects.DotNetDistributedApp_AppHost>,
            """
            Nothing has touched Docker yet - this stage only builds the app model in process. A timeout here points at
            the AppHost project rather than at infrastructure: a resource whose configuration blocks (a parameter with
            no value, so Aspire waits for a prompt that never comes), or a hang resolving the AppHost assembly.
            """
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

        App = await RunStartupStage(
            "Building the AppHost",
            AppHostBuildTimeout,
            appHost.BuildAsync,
            """
            Still no containers: this builds the DI container and the resource graph. A timeout here is a hang inside
            AppHost.cs - most likely an eventing callback (BeforeStartEvent and friends) that never returns.
            """
        );

        await RunStartupStage(
            "Starting the AppHost resources",
            AppHostStartTimeout,
            App.StartAsync,
            """
            This is where the Docker work happens: every container and project resource is created. The usual causes
            are environmental rather than anything in this repository.

              - The Docker daemon is not running, or is still starting. Aspire retries rather than failing fast, so a
                stopped daemon reaches you as this timeout and not as a connection error. Check `docker info`.
              - A cold image cache. The first pull of Postgres, Kafka, Valkey and GeoIP can outrun the budget above on
                a slow connection; a re-run usually succeeds because the images are local by then.
              - A port already bound, most often by containers left behind by an aborted run: `docker ps`.
            """
        );

        await RunStartupStage(
            $"Waiting for {ResourceNames.Api} to become healthy",
            ResourceHealthyTimeout,
            token => App.ResourceNotifications.WaitForResourceHealthyAsync(ResourceNames.Api, token),
            $"""
            {ResourceNames.Api} is deliberately the last resource to become healthy: it waits for the database,
            then for the migration service to run to *completion*, then for spatial-api, geoip, the cache and kafka.
            Any one of those stuck leaves it in Waiting indefinitely, so read the resource states below before
            assuming the API itself is at fault - a migration service still in Running has not finished, and one in
            FailedToStart (usually a migration that throws) means this wait can never complete.
            """
        );

        await RunStartupStage(
            "Starting the test Kafka bus",
            KafkaBusStartTimeout,
            ConfigureKafkaServices,
            $"""
            The producer and mock-handler bus (provider #1). It creates the {Topics.Common} and {Topics.CommonDlq}
            topics and joins two consumer groups, so it is the first stage that needs the broker to accept connections
            rather than merely be running. A container that is up but whose advertised listener is unreachable from the
            test process fails exactly here.
            """
        );

        await RunStartupStage(
            "Starting the in-process events consumer pipeline",
            KafkaBusStartTimeout,
            ConfigureEventsConsumerServices,
            """
            The real AddEventsConsumerKafka pipeline (provider #2). It needs Kafka *and* the Postgres database for the
            deduplication inbox, and it is the first stage to open a database connection from the test process - so a
            database that accepted the migration service but not this connection string shows up here.

            This stage can also fail without timing out: its provider validates scopes and validates on build, so a
            handler registered singleton throws an InvalidOperationException naming the offending service instead.
            """
        );
    }

    /// <summary>
    /// An unlinked wall-clock deadline, for this fixture's own lifecycle. Tests should use
    /// <c>TestContext.Current.CancellationToken</c> or <see cref="CreateDeadline"/> instead.
    /// </summary>
    /// <remarks>
    /// <see cref="DisposeAsync"/> deliberately does not link to <see cref="TestContext"/>'s token: after an aborted
    /// run that token is already cancelled, and <see cref="PurgeProcessedWeatherEvents"/> would silently skip.
    /// </remarks>
    private static CancellationToken CreateCancellationToken(TimeSpan? timeout = null) =>
        new CancellationTokenSource(timeout ?? DefaultTimeout).Token;

    /// <summary>
    /// A deadline for a wait that would otherwise hang forever — <see cref="WaitForDeadLetteredEvent"/>, or anything
    /// else awaiting a signal rather than a bounded round trip. Linked to <see cref="TestContext"/>'s token, so an
    /// aborted run ends the wait as well as the deadline expiring.
    /// </summary>
    /// <remarks>
    /// For ordinary async calls in a test — EF queries, <c>SaveChangesAsync</c>, HTTP requests — pass
    /// <c>TestContext.Current.CancellationToken</c> directly; those are already bounded and need no deadline.
    /// </remarks>
    public static CancellationTokenSource CreateDeadline(TimeSpan? timeout = null)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);

        return deadline;
    }

    /*
     * Runs one startup stage under its own deadline and turns a timeout into a message that names the stage.
     *
     * The token goes to the operation AND to WaitAsync on purpose: the token covers an operation that observes
     * cancellation, the WaitAsync timeout covers one that does not - which is what the `.WaitAsync(DefaultTimeout, …)`
     * calls this replaced were guarding against.
     */
    private async Task<T> RunStartupStage<T>(
        string stage,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> operation,
        string diagnosis
    )
    {
        using var deadline = new CancellationTokenSource(timeout);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            return await operation(deadline.Token).WaitAsync(timeout, deadline.Token);
        }
        catch (Exception exception)
            when (exception is TimeoutException
                || (exception is OperationCanceledException && deadline.IsCancellationRequested)
            )
        {
            throw new TimeoutException(await DescribeStartupTimeout(stage, timeout, startedAt, diagnosis), exception);
        }
    }

    private async Task RunStartupStage(
        string stage,
        TimeSpan timeout,
        Func<CancellationToken, Task> operation,
        string diagnosis
    ) =>
        await RunStartupStage<object?>(
            stage,
            timeout,
            async token =>
            {
                await operation(token);

                return null;
            },
            diagnosis
        );

    private Task RunStartupStage(
        string stage,
        TimeSpan timeout,
        Func<CancellationToken, ValueTask> operation,
        string diagnosis
    ) => RunStartupStage(stage, timeout, token => operation(token).AsTask(), diagnosis);

    private async Task<string> DescribeStartupTimeout(
        string stage,
        TimeSpan timeout,
        long startedAt,
        string diagnosis
    ) =>
        $"""
            The integration test AppHost never finished starting, so every test in this assembly is reported as failed.

            Stage:   {stage}
            Gave up: after {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1}s (budget {timeout.TotalSeconds:F0}s)

            {diagnosis}

            Resource states when the deadline expired:
            {await DescribeResourceStates()}

            Aspire's own logs carry the detail this message cannot: the fixture raises the AppHost to Debug, so the failed
            stage is usually explained a few lines above this exception in the test output.
            """;

    /*
     * The states the Aspire dashboard would be showing, for a message that has to stand in for it. WatchAsync replays
     * the latest snapshot it holds for every resource before it starts streaming - the same replay that lets
     * WaitForResourceHealthyAsync return for a resource that is already healthy - so a brief drain is enough.
     */
    private async Task<string> DescribeResourceStates()
    {
        if (App is null)
        {
            return "  (the AppHost does not exist yet, so it has no resources)";
        }

        using var deadline = new CancellationTokenSource(ResourceStateDrainTimeout);
        var states = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            await foreach (var resourceEvent in App.ResourceNotifications.WatchAsync(deadline.Token))
            {
                var health = resourceEvent.Snapshot.HealthStatus is { } healthStatus
                    ? $", health {healthStatus}"
                    : string.Empty;
                states[resourceEvent.ResourceId] = $"{resourceEvent.Snapshot.State?.Text ?? "(no state)"}{health}";
            }
        }
        // The drain always ends this way: the stream only completes when the application shuts down.
        catch (OperationCanceledException) { }

        return states.Count == 0
            ? "  (none reported - the resources were never started)"
            : string.Join(
                Environment.NewLine,
                states.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"  {x.Key}: {x.Value}")
            );
    }

    /*
     * Every field here can still be null. A startup stage that times out leaves the fixture half-built, and each of
     * these `null!` fields is only assigned by the stage that created it - so an unguarded teardown would throw a
     * NullReferenceException over the top of the message that explains what actually went wrong.
     */
    public async ValueTask DisposeAsync()
    {
        if (_eventsConsumerKafkaBus is not null)
        {
            await _eventsConsumerKafkaBus.StopAsync();
        }

        if (_eventsConsumerServiceProvider is not null)
        {
            await PurgeProcessedWeatherEvents();
            await _eventsConsumerServiceProvider.DisposeAsync();
        }

        if (_kafkaBus is not null)
        {
            await _kafkaBus.StopAsync();
        }

        if (_kafkaServiceProvider is not null)
        {
            await _kafkaServiceProvider.DisposeAsync();
        }

        if (App is not null)
        {
            await App.DisposeAsync();
        }

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
            .AddSingleton<UnreadableDeadLetterRecorder>()
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
                                    .AddMiddlewares(m => m.AddSerializer<JsonCoreSerializer>())
                            )
                            /*
                             * No serializer middleware, so ProduceAsync takes a byte[] and puts it on the topic
                             * verbatim. That is the only way to produce a message the consumer cannot read - a missing
                             * Message-Type header, or a body that is not valid JSON - which is what
                             * UnreadableMessageDeadLetteringShould needs.
                             */
                            .AddProducer<UnreadableEventProducer>(producer =>
                                producer.DefaultTopic(Topics.Common).WithAcks(Acks.All)
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
                                    .WithBufferSize(100) // In-memory buffer size per worker
                                    .WithWorkersCount(1) // Number of concurrent processing threads
                                    .WithAutoOffsetReset(AutoOffsetReset.Earliest) // Start from beginning if no offset exists
                                    .WithConsumerConfig(
                                        new ConsumerConfig
                                        {
                                            // EnableAutoCommit is not set: ConsumerConfigurationBuilder.Build overwrites it regardless. KafkaFlow owns committing.
                                            MaxPollIntervalMs = 300000, // Max processing time (5 mins) before broker assumes consumer is dead
                                            SessionTimeoutMs = 10000, // Time before broker detects a silent consumer crash
                                        }
                                    )
                                    .AddMiddlewares(middlewares =>
                                        middlewares
                                            // Before the deserializer on purpose: a dead letter whose Message-Type
                                            // header is missing or whose body is malformed never gets past it, and
                                            // those are exactly the messages UnreadableDeadLetterRecorder exists for.
                                            .Add<UnreadableDeadLetterRecordingMiddleware>()
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
                                    .WithBufferSize(100) // In-memory buffer size per worker
                                    .WithWorkersCount(1) // Number of concurrent processing threads
                                    .WithAutoOffsetReset(AutoOffsetReset.Earliest) // Start from beginning if no offset exists
                                    .WithConsumerConfig(
                                        new ConsumerConfig
                                        {
                                            // EnableAutoCommit is not set: ConsumerConfigurationBuilder.Build overwrites it regardless. KafkaFlow owns committing.
                                            MaxPollIntervalMs = 300000, // Max processing time (5 mins) before broker assumes consumer is dead
                                            SessionTimeoutMs = 10000, // Time before broker detects a silent consumer crash
                                        }
                                    )
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
        // An aborted run cancels a CreateDeadline token too, and reporting that as a timeout would bury the real
        // reason the test stopped.
        catch (OperationCanceledException exception)
            when (!TestContext.Current.CancellationToken.IsCancellationRequested)
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

    /// <summary>
    /// Completes once the in-process events consumer pipeline has dead lettered a message it could not deserialize,
    /// returning the headers the dead letter carried. Matched on the exact payload bytes, which only works because
    /// <see cref="RetryDeadLetterMiddleware"/> forwards the original message untouched.
    /// </summary>
    /// <remarks>
    /// Filtered by consumer group for the same reason as <see cref="WaitForDeadLetteredEvent"/>: the out-of-process
    /// <c>events-consumer</c> service dead letters its own copy of the same message and can get there first.
    /// </remarks>
    public async Task<IMessageHeaders> WaitForUnreadableDeadLetter(string payload, CancellationToken cancellationToken)
    {
        try
        {
            return await _kafkaServiceProvider
                .GetRequiredService<UnreadableDeadLetterRecorder>()
                .WaitFor(EventsConsumerGroupId, payload, cancellationToken);
        }
        catch (OperationCanceledException exception)
            when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"""
                Timed out waiting for an unreadable message to reach {Topics.CommonDlq} under consumer group
                {EventsConsumerGroupId}.

                This is the failure mode the pipeline order exists to prevent. If RetryDeadLetterMiddleware is
                registered inside the deserializer rather than outside it, a message that fails to deserialize throws
                past retry/DLQ into KafkaFlow's ConsumerWorker, which logs it, swallows it, and stores the offset
                anyway - so nothing ever arrives here. The same happens if the deserializer resolves message types with
                KafkaFlow's DefaultTypeResolver instead of StrictMessageTypeResolver, which returns null for an
                unreadable Message-Type header and is answered with a bare return.

                A payload mismatch would also time out: the dead letter must be the original bytes, so re-serializing
                the message on the way to the DLQ - by giving the DLQ producer a serializer middleware - breaks this.
                """,
                exception
            );
        }
    }

    public IMessageProducer<T> GetMessageProducer<T>() =>
        _kafkaServiceProvider.GetRequiredService<IMessageProducer<T>>();

    public IMessageHandler<T> GetMessageHandler<T>() => _kafkaServiceProvider.GetRequiredService<IMessageHandler<T>>();
}
