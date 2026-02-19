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
        using var producer = await appHostFixture.CreateEventProducer<string, Event1PayloadDto>(ct);
        var service = new EventsService<Event1PayloadDto>(
            producer,
            new MetricsService(appHostFixture.App.Services.GetRequiredService<IMeterFactory>()),
            new NullLogger<EventsService<Event1PayloadDto>>()
        );

        await service.SendEvent(topic, new Event1PayloadDto(expectedKey, expectedValue));

        using var consumer = await appHostFixture.CreateEventConsumer<string, Event1PayloadDto>(topic, ct);
        var consumeResult = consumer.Consume(ct);
        consumeResult.Message.Should().NotBeNull();
        consumeResult.Message.Key.Should().Be(expectedKey);
        consumeResult.Message.Value.Should().BeEquivalentTo(new Event1PayloadDto(expectedKey, expectedValue));
    }
}
