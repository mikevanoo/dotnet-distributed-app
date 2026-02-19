using AwesomeAssertions;
using Confluent.Kafka;

namespace DotNetDistributedApp.IntegrationTests.Api.Events;

public class EventsServiceShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task SendEventToKakfa()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var topic = "apphost-e2e-topic";

        // Ask the running AppHost for the connection string of the "messaging" resource
        var bootstrapServers =
            await appHostFixture.App.GetConnectionStringAsync("events", ct)
            ?? throw new InvalidOperationException("Kafka connection string not found.");

        var producerConfig = new ProducerConfig { BootstrapServers = bootstrapServers };
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = "apphost-test-consumer-group",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        var expectedKey = "user-789";
        var expectedValue = "{\"status\": \"apphost-verified\"}";

        // Act: Produce
        using (var producer = new ProducerBuilder<string, string>(producerConfig).Build())
        {
            var message = new Message<string, string> { Key = expectedKey, Value = expectedValue };
            var deliveryResult = await producer.ProduceAsync(topic, message, ct);

            deliveryResult.Status.Should().Be(PersistenceStatus.Persisted);
        }

        // Assert: Consume
        using (var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build())
        {
            consumer.Subscribe(topic);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var consumeResult = consumer.Consume(cts.Token);

            consumeResult.Should().NotBeNull();
            consumeResult.Message.Key.Should().Be(expectedKey);
            consumeResult.Message.Value.Should().Be(expectedValue);

            consumer.Close();
        }
    }
}
