using System.Text.Json;
using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetDistributedApp.Events.Consumer;

public partial class EventsConsumer<T>(
    IConsumer<string, Event1PayloadDto> eventConsumer,
    IMetricsService metricsService,
    ILogger<EventsConsumer<T>> logger
) : BackgroundService
    where T : BaseEventPayloadDto
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogSubscribingToTopic(Topics.Common);
        eventConsumer.Subscribe(Topics.Common);

        while (!stoppingToken.IsCancellationRequested)
        {
            var consumeResult = eventConsumer.Consume(stoppingToken);
            var topic = consumeResult.Topic;
            var eventName = consumeResult.Message.Value.EventName;

            switch (eventName)
            {
                case "event1":
                    metricsService.ConsumeEventSuccess(1, topic, eventName);
                    var valueJson = JsonSerializer.Serialize(consumeResult.Message.Value);
                    LogConsumedMessage(eventName, valueJson);
                    break;
                default:
                    metricsService.ConsumeEventUnrecognised(1, topic, eventName);
                    break;
            }

            eventConsumer.Commit();
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(LogLevel.Information, "Subscribing to topic: {Topic}")]
    private partial void LogSubscribingToTopic(string topic);

    [LoggerMessage(LogLevel.Information, "Consumed message: {EventName} - {Value}")]
    private partial void LogConsumedMessage(string eventName, string value);
}
