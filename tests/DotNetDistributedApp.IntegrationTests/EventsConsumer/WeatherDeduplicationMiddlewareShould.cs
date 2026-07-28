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
    public async Task RecordSingleInboxRowNoDuplicateMetricForSingleEventConsumed()
    {
        var cancellationToken = AppHostFixture.CreateCancellationToken();
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "inbox-row-probe");
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
                (await GetInboxRows(payload.EventId, cancellationToken)).Should().ContainSingle();
            })
            .Should()
            .NotThrowAfterAsync(30.Seconds(), 250.Milliseconds());

        var row = (await GetInboxRows(payload.EventId, cancellationToken)).Single();
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
    public async Task RecordSingleInboxRowWithDuplicateMetricForDuplicateEventsConsumed()
    {
        var cancellationToken = AppHostFixture.CreateCancellationToken();
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "inbox-row-probe");
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
                (await GetInboxRows(payload.EventId, cancellationToken)).Should().ContainSingle();
            })
            .Should()
            .NotThrowAfterAsync(30.Seconds(), 250.Milliseconds());

        var row = (await GetInboxRows(payload.EventId, cancellationToken)).Single();
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
    public async Task NotRecordInboxRowWhenConsumedEventThrows()
    {
        var cancellationToken = AppHostFixture.CreateCancellationToken();
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        using var duplicateMetrics = new MetricCollector<int>(
            appHostFixture.EventsConsumerServices.GetRequiredService<IMeterFactory>(),
            MetricsService.MeterName,
            "events.consume_duplicate"
        );

        var producer = appHostFixture.GetMessageProducer<EventsService>();
        await producer.ProduceAsync(Topics.Common, payload.PartitionKey, payload);

        await appHostFixture.WaitForDeadLetteredEvent(payload.EventId, cancellationToken);

        (await GetInboxRows(payload.EventId, cancellationToken)).Should().BeEmpty();

        duplicateMetrics
            .GetMeasurementSnapshot()
            .Should()
            .NotContain(
                x => x.Tags["event_name"] as string == payload.EventName,
                "an event handler that throws should not post a duplicate metric"
            );
    }

    private async Task<List<ProcessedWeatherEvent>> GetInboxRows(Guid eventId, CancellationToken cancellationToken)
    {
        await using var scope = appHostFixture.CreateEventsConsumerScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WeatherDbContext>();

        return await dbContext
            .ProcessedWeatherEvents.AsNoTracking()
            .Where(x => x.ConsumerGroup == appHostFixture.EventsConsumerGroupId && x.Id == eventId)
            .ToListAsync(cancellationToken);
    }
}
