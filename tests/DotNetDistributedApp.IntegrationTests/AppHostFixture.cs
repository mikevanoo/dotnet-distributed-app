using Aspire.Hosting;
using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.IntegrationTests;
using Microsoft.Extensions.Logging;

[assembly: AssemblyFixture(typeof(AppHostFixture))]

namespace DotNetDistributedApp.IntegrationTests;

public class AppHostFixture : IAsyncLifetime
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public DistributedApplication App { get; private set; } = null!;

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
            .ResourceNotifications.WaitForResourceHealthyAsync("api", cancellationToken)
            .WaitAsync(AppHostFixture.DefaultTimeout, cancellationToken);
    }

    public static CancellationToken CreateCancellationToken(TimeSpan? timeout = null) =>
        new CancellationTokenSource(timeout ?? DefaultTimeout).Token;

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    public async Task<IProducer<TKey, TValue>> CreateEventProducer<TKey, TValue>(CancellationToken cancellationToken)
        where TValue : new()
    {
        var bootstrapServers = await GetKafkaConnectionString(cancellationToken);
        var producerConfig = new ProducerConfig { BootstrapServers = bootstrapServers };
        return new ProducerBuilder<TKey, TValue>(producerConfig)
            .SetValueSerializer(new EventJsonSerializer<TValue>())
            .Build();
    }

    public async Task<IConsumer<TKey, TValue>> CreateEventConsumer<TKey, TValue>(CancellationToken cancellationToken)
        where TValue : new()
    {
        var bootstrapServers = await GetKafkaConnectionString(cancellationToken);
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = Guid.NewGuid().ToString(),
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };
        return new ConsumerBuilder<TKey, TValue>(consumerConfig)
            .SetValueDeserializer(new EventJsonSerializer<TValue>())
            .Build();
    }

    private async Task<string> GetKafkaConnectionString(CancellationToken cancellationToken) =>
        await App.GetConnectionStringAsync("events", cancellationToken)
        ?? throw new InvalidOperationException("Kafka connection string not found.");
}
