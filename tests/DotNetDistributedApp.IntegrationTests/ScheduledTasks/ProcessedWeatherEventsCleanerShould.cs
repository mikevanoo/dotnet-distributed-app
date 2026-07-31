using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.ScheduledTasks.ProcessedWeatherEvents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotNetDistributedApp.IntegrationTests.ScheduledTasks;

public class ProcessedWeatherEventsCleanerShould(AppHostFixture appHostFixture)
{
    private readonly DateTimeOffset _utcNow = DateTimeOffset.UtcNow;
    private readonly string _eventName = $"test-event-{Guid.NewGuid()}";
    private readonly IMetricsService _metricsService = Substitute.For<IMetricsService>();

    [Fact]
    public async Task CleanOldOverriddenProcessedEvents()
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();
        var options = Options.Create(
            new ProcessedWeatherEventsCleanerOptions
            {
                DefaultRetention = TimeSpan.FromDays(365),
                RetentionByEventName = { [_eventName] = TimeSpan.FromMinutes(5) },
            }
        );
        var cleaner = CreateCleaner(options, dbContext);

        var eventToBeDeleted = CreateProcessedEvent(
            _eventName,
            appHostFixture.EventsConsumerGroupId,
            _utcNow.Subtract(TimeSpan.FromMinutes(10))
        );
        var eventToKeep = CreateProcessedEvent(_eventName, appHostFixture.EventsConsumerGroupId, _utcNow);
        await AddProcessedEvents(eventToBeDeleted, eventToKeep);

        await cleaner.Invoke();

        var remaining = await GetProcessedEvents(_eventName, TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle();
        remaining.Single().Id.Should().Be(eventToKeep.Id);
        _metricsService.Received(1).ProcessedEventDeleted(1, _eventName);
    }

    [Fact]
    public async Task CleanOldNonOverriddenProcessedEvents()
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();
        var options = Options.Create(
            new ProcessedWeatherEventsCleanerOptions
            {
                DefaultRetention = TimeSpan.FromMinutes(5),
                RetentionByEventName =
                {
                    [new SimpleEventPayloadDto().EventName] = TimeSpan.FromDays(365),
                    [new FailingEventPayloadDto().EventName] = TimeSpan.FromDays(365),
                },
            }
        );
        var cleaner = CreateCleaner(options, dbContext);

        var eventToBeDeleted = CreateProcessedEvent(
            _eventName,
            appHostFixture.EventsConsumerGroupId,
            _utcNow.Subtract(TimeSpan.FromMinutes(10))
        );
        var eventToKeep = CreateProcessedEvent(_eventName, appHostFixture.EventsConsumerGroupId, _utcNow);
        await AddProcessedEvents(eventToBeDeleted, eventToKeep);

        await cleaner.Invoke();

        var remaining = await GetProcessedEvents(_eventName, TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle();
        remaining.Single().Id.Should().Be(eventToKeep.Id);
        // stale rows from earlier runs may be swept in the same call, so the count is not exactly 1
        _metricsService.Received(1).ProcessedEventDeleted(Arg.Is<int>(count => count >= 1), "other");
    }

    private ProcessedWeatherEventsCleaner CreateCleaner(
        IOptions<ProcessedWeatherEventsCleanerOptions> options,
        WeatherDbContext dbContext
    )
    {
        var cleaner = new ProcessedWeatherEventsCleaner(
            options,
            dbContext,
            _metricsService,
            Substitute.For<ILogger<ProcessedWeatherEventsCleaner>>()
        )
        {
            // Coravel sets this from ICancellableInvocable; without it the deletes under test are uncancellable.
            CancellationToken = TestContext.Current.CancellationToken,
        };
        return cleaner;
    }

    private async Task AddProcessedEvents(params ProcessedWeatherEvent[] events)
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();
        // Passing the token also picks the AddRangeAsync(IEnumerable<T>, CancellationToken) overload; an array on its
        // own binds to AddRangeAsync(params T[]), which has no token parameter and no analyzer warning either.
        await dbContext.ProcessedWeatherEvents.AddRangeAsync(events, TestContext.Current.CancellationToken);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<List<ProcessedWeatherEvent>> GetProcessedEvents(
        string eventName,
        CancellationToken cancellationToken
    )
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();
        return await dbContext
            .ProcessedWeatherEvents.AsNoTracking()
            .Where(x => x.ConsumerGroup == appHostFixture.EventsConsumerGroupId && x.EventName == eventName)
            .ToListAsync(cancellationToken);
    }

    private static ProcessedWeatherEvent CreateProcessedEvent(
        string eventName,
        string consumerGroup,
        DateTimeOffset processedAtUtc
    ) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventName = eventName,
            Topic = "test-topic",
            PartitionKey = "test-key",
            ConsumerGroup = consumerGroup,
            Partition = 0,
            Offset = 0,
            ProcessedAtUtc = processedAtUtc,
        };
}
