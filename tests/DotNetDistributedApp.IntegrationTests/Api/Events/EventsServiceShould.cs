using System.Diagnostics.Metrics;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetDistributedApp.IntegrationTests.Api.Events;

public class EventsServiceShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task SendEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var topic = Guid.NewGuid().ToString();
        var expectedKey = "user-789";
        var expectedValue = "{\"status\": \"all-good\"}";
        using var producer = await appHostFixture.CreateEventProducer<string, SimpleEventPayloadDto>(ct);
        var service = new EventsService<SimpleEventPayloadDto>(
            producer,
            new MetricsService(appHostFixture.App.Services.GetRequiredService<IMeterFactory>()),
            new NullLogger<EventsService<SimpleEventPayloadDto>>()
        );

        await service.SendEvent(topic, new SimpleEventPayloadDto(expectedKey, expectedValue));

        using var consumer = await appHostFixture.CreateEventConsumer<string, SimpleEventPayloadDto>(topic, ct);
        var consumeResult = consumer.Consume(ct);
        consumeResult.Message.Should().NotBeNull();
        consumeResult.Message.Key.Should().Be(expectedKey);
        consumeResult.Message.Value.Should().BeEquivalentTo(new SimpleEventPayloadDto(expectedKey, expectedValue));
    }
}
