using System.Text.Json;
using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.EventsConsumer;

public class EventsConsumer<T>(
    IConsumer<string, Event1PayloadDto> eventConsumer,
    IMetricsService metricsService,
    ILogger<EventsConsumer<T>> logger
) : BackgroundService
    where T : BaseEventPayloadDto
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Subscribing to topic: {Topic}", Topics.Common);
        eventConsumer.Subscribe(Topics.Common);

        while (!stoppingToken.IsCancellationRequested)
        {
            var consumeResult = eventConsumer.Consume(stoppingToken);

            var valueJson = JsonSerializer.Serialize(consumeResult.Message.Value);
            logger.LogInformation("Consumed message: {Key} - {Value}", consumeResult.Message.Key, valueJson);

            eventConsumer.Commit();
        }

        return Task.CompletedTask;
    }
}
