using Aspire.Hosting;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.IntegrationTests;
using DotNetDistributedApp.IntegrationTests.Api.Events;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using KafkaFlow.Serializer;
using Microsoft.Extensions.Logging;
using NSubstitute;

[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace DotNetDistributedApp.IntegrationTests;

public class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public DistributedApplication App { get; private set; } = null!;
    private ServiceProvider _kafkaServiceProvider = null!;
    private IKafkaBus _kafkaBus = null!;

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
    }

    public static CancellationToken CreateCancellationToken(TimeSpan? timeout = null) =>
        new CancellationTokenSource(timeout ?? DefaultTimeout).Token;

    public async ValueTask DisposeAsync()
    {
        await _kafkaBus.StopAsync();
        await _kafkaServiceProvider.DisposeAsync();
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private async ValueTask ConfigureKafkaServices(CancellationToken cancellationToken)
    {
        // The real connection string is only known after the Kafka container has started.
        var kafkaConnectionString = await App.GetConnectionStringAsync(ResourceNames.Events, cancellationToken);
        var kafkaServices = new ServiceCollection();

        kafkaServices
            .AddLogging()
            .AddSingleton(Substitute.For<IMessageHandler<TestMessage>>())
            .AddKafka(kafka =>
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

    public IMessageProducer<T> GetMessageProducer<T>() =>
        _kafkaServiceProvider.GetRequiredService<IMessageProducer<T>>();

    public IMessageHandler<T> GetMessageHandler<T>() => _kafkaServiceProvider.GetRequiredService<IMessageHandler<T>>();
}
