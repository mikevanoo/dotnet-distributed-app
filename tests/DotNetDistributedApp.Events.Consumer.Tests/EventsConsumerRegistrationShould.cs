using AwesomeAssertions;
using DotNetDistributedApp.ServiceDefaults;
using KafkaFlow;
using Microsoft.Extensions.Configuration;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class EventsConsumerRegistrationShould
{
    [Fact]
    public void RegisterMessageHandlersAsScopedSoTheyShareTheDeduplicationTransaction()
    {
        var services = new ServiceCollection().AddEventsConsumerKafka(BuildConfiguration());

        var handlerRegistrations = services
            .Where(x => typeof(IMessageHandler).IsAssignableFrom(x.ServiceType))
            .ToList();

        handlerRegistrations.Should().NotBeEmpty("the consumer must register at least one message handler");
        handlerRegistrations
            .Should()
            .AllSatisfy(registration =>
                registration
                    .Lifetime.Should()
                    .Be(
                        ServiceLifetime.Scoped,
                        $"""
                        {registration.ServiceType.Name} must resolve from the same per-message DI scope as
                        WeatherDeduplicationMiddleware so its writes join that middleware's transaction.
                        KafkaFlow defaults handlers to InstanceLifetime.Singleton, so every AddTypedHandlers
                        call needs an explicit WithHandlerLifetime(InstanceLifetime.Scoped).
                        """
                    )
            );
    }

    [Fact]
    public void MapEachPayloadTypeToExactlyOneHandler()
    {
        var handlersByPayloadType = typeof(SimpleEventMessageHandler)
            .Assembly.GetTypes()
            .Where(x => x is { IsClass: true, IsAbstract: false } && typeof(IMessageHandler).IsAssignableFrom(x))
            .SelectMany(handler =>
                handler
                    .GetInterfaces()
                    .Where(x => x.IsGenericType && typeof(IMessageHandler).IsAssignableFrom(x))
                    .Select(x => new { PayloadType = x.GenericTypeArguments[0], Handler = handler })
            )
            .GroupBy(x => x.PayloadType)
            .ToList();

        handlersByPayloadType.Should().NotBeEmpty("the consumer must define at least one message handler");
        handlersByPayloadType
            .Should()
            .AllSatisfy(group =>
                group
                    .Should()
                    .ContainSingle(
                        $"""
                        {group.Key.Name} must have exactly one handler.
                        KafkaFlow's TypedHandlerMiddleware runs all handlers for a payload type concurrently via
                        Task.WhenAll, and handlers are scoped, so two handlers would share one WeatherDbContext in
                        parallel - which is not thread-safe. Either merge the handlers, or give the second one its
                        own DbContext and reconsider the single-transaction guarantee in KAFKA-IDEMPOTENCY-PLAN.md.
                        """
                    )
            );
    }

    private static ConfigurationManager BuildConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { [$"ConnectionStrings:{ResourceNames.Events}"] = "localhost:9092" }
        );
        return configuration;
    }
}
