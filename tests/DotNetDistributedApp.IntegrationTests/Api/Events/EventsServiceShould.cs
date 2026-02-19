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
    public async Task SendEventToKakfa()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topic = Guid.NewGuid().ToString();

        var bootstrapServers =
            await appHostFixture.App.GetConnectionStringAsync("events", ct)
            ?? throw new InvalidOperationException("Kafka connection string not found.");

        var producerConfig = new ProducerConfig { BootstrapServers = bootstrapServers };
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = Guid.NewGuid().ToString(),
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        var expectedKey = "user-789";
        var expectedValue = "{\"status\": \"apphost-verified\"}";

        // Act
        using (
            var producer = new ProducerBuilder<string, Event1PayloadDto>(producerConfig)
                .SetValueSerializer(new EventJsonSerializer<Event1PayloadDto>())
                .Build()
        )
        {
            var service = new EventsService<Event1PayloadDto>(
                producer,
                new MetricsService(appHostFixture.App.Services.GetRequiredService<IMeterFactory>()),
                new NullLogger<EventsService<Event1PayloadDto>>()
            );

            await service.SendEvent(topic, new Event1PayloadDto(expectedKey, expectedValue));
        }

        // Assert
        using (
            var consumer = new ConsumerBuilder<string, Event1PayloadDto>(consumerConfig)
                .SetValueDeserializer(new EventJsonSerializer<Event1PayloadDto>())
                .Build()
        )
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
