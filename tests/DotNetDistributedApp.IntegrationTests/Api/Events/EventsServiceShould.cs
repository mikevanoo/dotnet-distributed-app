using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Confluent.Kafka;
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
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topic = Guid.NewGuid().ToString();
        var expectedKey = "user-789";
        var expectedValue = "{\"status\": \"apphost-verified\"}";

        // Act
        using (var producer = await appHostFixture.CreateEventProducer<string, Event1PayloadDto>(ct))
        {
            var service = new EventsService<Event1PayloadDto>(
                producer,
                new MetricsService(appHostFixture.App.Services.GetRequiredService<IMeterFactory>()),
                new NullLogger<EventsService<Event1PayloadDto>>()
            );

            await service.SendEvent(topic, new Event1PayloadDto(expectedKey, expectedValue));
        }

        // Assert
        using (var consumer = await appHostFixture.CreateEventConsumer<string, Event1PayloadDto>(ct))
        {
            consumer.Subscribe(topic);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var consumeResult = consumer.Consume(cts.Token);

            consumeResult.Should().NotBeNull();
            consumeResult.Message.Key.Should().Be(expectedKey);
            consumeResult.Message.Value.Should().BeEquivalentTo(new Event1PayloadDto(expectedKey, expectedValue));

            consumer.Close();
        }
    }
}
