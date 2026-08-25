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
        /*
         * Read this as "no simple event was duplicated anywhere", not "this event was not duplicated". EventName is a
         * constant on the payload type and the duplicate counter carries only topic and event_name, so the snapshot
         * holds no way to tell one simple-event duplicate from another - the collector sees every measurement the
         * in-process pipeline records while it is open.
         *
         * Two facts about the suite, not this test, are what keep that honest: xUnit runs a class's methods serially,
         * so the duplicate test below cannot bleed into this collector, and nothing else in the assembly produces a
         * SimpleEventPayloadDto. Add a second simple-event producer and this assertion starts failing for reasons that
         * have nothing to do with the event it names.
         *
         * Unlike its Contain counterpart below, this one needs no barrier. Reading the snapshot at the earliest
         * possible moment is when a stray measurement is least likely to have arrived, so the timing that made the
         * duplicate test flaky biases this one towards passing instead.
         */
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
        /*
         * The inbox row is not a barrier for the duplicate metric, and the wait below is the one that is.
         *
         * The row appears when the FIRST delivery commits; the metric is only recorded when the SECOND reaches
         * WeatherDeduplicationMiddleware and is skipped. So "exactly one row exists" is satisfied in the gap between
         * the two deliveries, and reading the snapshot there sees nothing. The gap is normally sub-millisecond - the
         * consumer takes the second message off its buffer while this test is still running its row assertions - which
         * is why it survives locally and failed on CI.
         *
         * WaitForMeasurementsAsync awaits a signal rather than a bounded round trip, so it gets a deadline.
         */
        using var deadline = AppHostFixture.CreateDeadline();
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

        await duplicateMetrics.WaitForMeasurementsAsync(1, deadline.Token);

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
