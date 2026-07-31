using System.Diagnostics.Metrics;
using AwesomeAssertions;
using AwesomeAssertions.Extensions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace DotNetDistributedApp.IntegrationTests.EventsConsumer;

public class WeatherDeduplicationMiddlewareShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task RecordSingleProcessedEventNoDuplicateMetricForSingleEventConsumed()
    {
        // NotThrowAfterAsync below is what bounds this test; the token only needs to abort an in-flight query when
        // the run is cancelled, which is exactly what TestContext's token does.
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "processed-event-probe");
        using var duplicateMetrics = new MetricCollector<int>(
            appHostFixture.EventsConsumerServices.GetRequiredService<IMeterFactory>(),
            MetricsService.MeterName,
            "events.consume_duplicate"
        );

        await appHostFixture
            .GetMessageProducer<EventsService>()
            .ProduceAsync(Topics.Common, payload.PartitionKey, payload);

        await FluentActions
            .Awaiting(async () =>
            {
                (await GetProcessedEvents(payload.EventId, cancellationToken)).Should().ContainSingle();
            })
            .Should()
            .NotThrowAfterAsync(30.Seconds(), 250.Milliseconds());

        var row = (await GetProcessedEvents(payload.EventId, cancellationToken)).Single();
        row.EventName.Should().Be(payload.EventName);
        row.Topic.Should().Be(Topics.Common);
        row.PartitionKey.Should().Be(payload.PartitionKey);
        duplicateMetrics
            .GetMeasurementSnapshot()
            .Should()
            .NotContain(
                x => x.Tags["event_name"] as string == payload.EventName,
                "a single delivery should not post a duplicate metric"
            );
    }

    [Fact]
    public async Task RecordSingleProcessedEventRowWithDuplicateMetricForDuplicateEventsConsumed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "processed-event-probe");
        using var duplicateMetrics = new MetricCollector<int>(
            appHostFixture.EventsConsumerServices.GetRequiredService<IMeterFactory>(),
            MetricsService.MeterName,
            "events.consume_duplicate"
        );

        var producer = appHostFixture.GetMessageProducer<EventsService>();
        // produce duplicate events
        await producer.ProduceAsync(Topics.Common, payload.PartitionKey, payload);
        await producer.ProduceAsync(Topics.Common, payload.PartitionKey, payload);

        await FluentActions
            .Awaiting(async () =>
            {
                (await GetProcessedEvents(payload.EventId, cancellationToken)).Should().ContainSingle();
            })
            .Should()
            .NotThrowAfterAsync(30.Seconds(), 250.Milliseconds());

        var row = (await GetProcessedEvents(payload.EventId, cancellationToken)).Single();
        row.EventName.Should().Be(payload.EventName);
        row.Topic.Should().Be(Topics.Common);
        row.PartitionKey.Should().Be(payload.PartitionKey);
        duplicateMetrics
            .GetMeasurementSnapshot()
            .Should()
            .Contain(
                x => x.Tags["event_name"] as string == payload.EventName,
                "a duplicate delivery should post a duplicate metric"
            );
    }

    [Fact]
    public async Task NotRecordProcessedEventWhenConsumedEventThrows()
    {
        /*
         * The one test here that needs a deadline rather than TestContext's token: WaitForDeadLetteredEvent awaits a
         * TaskCompletionSource and nothing else bounds it, and the TimeoutException it throws - the message that
         * explains the "recorded the row anyway, so the retry succeeded and it was never dead lettered" failure mode -
         * only appears if the token trips.
         */
        using var deadline = AppHostFixture.CreateDeadline();
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        using var duplicateMetrics = new MetricCollector<int>(
            appHostFixture.EventsConsumerServices.GetRequiredService<IMeterFactory>(),
            MetricsService.MeterName,
            "events.consume_duplicate"
        );

        var producer = appHostFixture.GetMessageProducer<EventsService>();
        await producer.ProduceAsync(Topics.Common, payload.PartitionKey, payload);

        await appHostFixture.WaitForDeadLetteredEvent(payload.EventId, deadline.Token);

        (await GetProcessedEvents(payload.EventId, deadline.Token)).Should().BeEmpty();

        duplicateMetrics
            .GetMeasurementSnapshot()
            .Should()
            .NotContain(
                x => x.Tags["event_name"] as string == payload.EventName,
                "an event handler that throws should not post a duplicate metric"
            );
    }

    private async Task<List<ProcessedWeatherEvent>> GetProcessedEvents(
        Guid eventId,
        CancellationToken cancellationToken
    )
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();
        return await dbContext
            .ProcessedWeatherEvents.AsNoTracking()
            .Where(x => x.ConsumerGroup == appHostFixture.EventsConsumerGroupId && x.Id == eventId)
            .ToListAsync(cancellationToken);
    }
}
