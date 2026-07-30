using Coravel.Invocable;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotNetDistributedApp.ScheduledTasks.ProcessedWeatherEvents;

public partial class ProcessedWeatherEventsCleaner(
    IOptions<ProcessedWeatherEventsCleanerOptions> options,
    WeatherDbContext dbContext,
    IMetricsService metricsService,
    ILogger<ProcessedWeatherEventsCleaner> logger
) : IInvocable, ICancellableInvocable
{
    public CancellationToken CancellationToken { get; set; }

    public async Task Invoke()
    {
        LogStarted();

        var opts = options.Value;
        var utcNow = DateTimeOffset.UtcNow;
        var overriddenEventNames = new List<string>();

        // clean events with a specific retention period
        foreach (var eventName in opts.RetentionByEventName.Keys)
        {
            overriddenEventNames.Add(eventName);
            var retention = opts.RetentionByEventName[eventName];
            var deleteBefore = utcNow - retention;
            var deletedCount = await dbContext
                .ProcessedWeatherEvents.Where(e => e.EventName == eventName && e.ProcessedAtUtc < deleteBefore)
                .ExecuteDeleteAsync(CancellationToken);

            metricsService.ProcessedEventDeleted(deletedCount, eventName);
            LogDeletedCount(deletedCount, eventName);
        }

        // clean all other events
        var defaultDeleteBefore = utcNow - opts.DefaultRetention;
        var otherEventsDeletedCount = await dbContext
            .ProcessedWeatherEvents.Where(e =>
                !overriddenEventNames.Contains(e.EventName) && e.ProcessedAtUtc < defaultDeleteBefore
            )
            .ExecuteDeleteAsync(CancellationToken);

        metricsService.ProcessedEventDeleted(otherEventsDeletedCount, "other");
        LogDeletedCount(otherEventsDeletedCount, "other");

        LogCompleted();
    }

    [LoggerMessage(LogLevel.Information, "ProcessedWeatherEventsCleaner started")]
    private partial void LogStarted();

    [LoggerMessage(LogLevel.Information, "ProcessedWeatherEventsCleaner completed")]
    private partial void LogCompleted();

    [LoggerMessage(LogLevel.Information, "Deleted {Count} {EventName} events")]
    private partial void LogDeletedCount(int count, string eventName);
}
