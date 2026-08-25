using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data.Weather;
using KafkaFlow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DotNetDistributedApp.Events.Consumer;

public partial class WeatherDeduplicationMiddleware(
    WeatherDbContext dbContext,
    IMetricsService metricsService,
    ILogger<WeatherDeduplicationMiddleware> logger
) : IMessageMiddleware
{
    public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
    {
        // ConsumerMetricsMiddleware is registered directly outside this one and short-circuits any message whose value
        // is not a BaseEventPayloadDto, so this cast cannot fail. It owns that decision because it is also the thing
        // that counts events.consume_unrecognised. Pinned by
        // EventsConsumerRegistrationShould.RegisterTheMetricsMiddlewareOutsideTheDeduplicationMiddlewareSoItOwnsTheUnrecognisedPayloadDecision.
        var payload = (BaseEventPayloadDto)context.Message.Value;

        var cancellationToken = context.ConsumerContext.WorkerStopped;
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(
            async token =>
            {
                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

                var groupId = context.ConsumerContext.GroupId;
                var eventId = payload.EventId;
                var eventName = payload.EventName;
                var offset = context.ConsumerContext.Offset;
                var topic = context.ConsumerContext.Topic;

                var alreadyProcessed = await dbContext.ProcessedWeatherEvents.AnyAsync(
                    x => x.ConsumerGroup == groupId && x.Id == eventId,
                    token
                );

                if (alreadyProcessed)
                {
                    LogDuplicateEventSkipped(eventId, eventName, offset);
                    metricsService.ConsumeEventDuplicate(1, topic, eventName);
                    await transaction.RollbackAsync(token);
                    return; // handlers skipped; offset still commits
                }

                await next(context); // handler DB writes enlist in this transaction

                dbContext.ProcessedWeatherEvents.Add(
                    new ProcessedWeatherEvent
                    {
                        Id = eventId,
                        EventName = eventName,
                        Topic = topic,
                        PartitionKey = payload.PartitionKey,
                        ConsumerGroup = groupId,
                        Partition = context.ConsumerContext.Partition,
                        Offset = offset,
                        ProcessedAtUtc = DateTimeOffset.UtcNow,
                    }
                );

                try
                {
                    await dbContext.SaveChangesAsync(token);
                    await transaction.CommitAsync(token);
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // Backstop: a concurrent worker committed the same event between our check and insert.
                    LogConcurrentDuplicateDetected(eventId, eventName);
                    metricsService.ConsumeEventDuplicate(1, context.ConsumerContext.Topic, payload.EventName);
                    await transaction.RollbackAsync(token);
                }
            },
            cancellationToken
        );
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    [LoggerMessage(LogLevel.Information, "Duplicate event skipped: {EventId} {EventName} {Offset}")]
    partial void LogDuplicateEventSkipped(Guid eventId, string eventName, long offset);

    [LoggerMessage(LogLevel.Warning, "Concurrent duplicate event detected: {EventId} {EventName}")]
    partial void LogConcurrentDuplicateDetected(Guid eventId, string eventName);
}
