using System.Diagnostics;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace DotNetDistributedApp.Events.TopicInitialiser;

public class Worker(IServiceProvider serviceProvider, IHostApplicationLifetime hostApplicationLifetime)
    : BackgroundService
{
    public const string ActivitySourceName = "TopicInitialiser";
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = ActivitySource.StartActivity("Initialising topics", ActivityKind.Client);

        try
        {
            using var scope = serviceProvider.CreateScope();
            await InitialiseTopics(scope, stoppingToken);
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            throw;
        }

        hostApplicationLifetime.StopApplication();
    }

    private static async Task InitialiseTopics(IServiceScope scope, CancellationToken cancellationToken)
    {
        const string KafkaConnectionName = "events";

        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Worker>>();
        var config = new AdminClientConfig();

        try
        {
            var connectionString = scope
                .ServiceProvider.GetRequiredService<IConfiguration>()
                .GetConnectionString(KafkaConnectionName);
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentException($"Kafka connection string '{KafkaConnectionName}' not found.");
            }

            config.BootstrapServers = connectionString;
            using var adminClient = new AdminClientBuilder(config).Build();

            var topicSpecs = new[]
            {
                new TopicSpecification
                {
                    Name = "common",
                    NumPartitions = 3,
                    ReplicationFactor = 1, // 1 is fine for local dev, use 3 for production
                },
            };

            logger.LogInformation("Creating Kafka topics...");
            await adminClient.CreateTopicsAsync(topicSpecs);
            logger.LogInformation("Kafka topics created successfully.");
        }
        catch (CreateTopicsException e)
        {
            // Check if the error is "Topic already exists"
            if (e.Results.All(r => r.Error.Code == ErrorCode.TopicAlreadyExists))
            {
                logger.LogWarning("Topics already exist. Skipping creation.");
            }
            else
            {
                logger.LogError(e, "An error occurred creating Kafka topics.");
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "An unexpected error occurred during topic initialization.");
        }
    }
}
