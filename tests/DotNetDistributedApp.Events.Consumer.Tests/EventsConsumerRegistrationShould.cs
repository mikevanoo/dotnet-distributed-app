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

    /*
     * Asserted through DI registration order because KafkaFlowConfigurator keeps the built KafkaConfiguration private.
     * MiddlewareConfigurationBuilder.AddAt registers each middleware's dependencies into the container at the moment it
     * appends the middleware to the pipeline, so the order of these descriptors is the order of the pipeline.
     * RetryDeadLetterMiddleware is registered by .Add<T>(); StrictMessageTypeResolver by .AddDeserializer<,>().
     */
    [Fact]
    public void RegisterTheRetryMiddlewareOutsideTheDeserializerSoUnreadableMessagesAreDeadLettered()
    {
        var services = new ServiceCollection().AddEventsConsumerKafka(BuildConfiguration());

        var retryPosition = IndexOf(services, typeof(RetryDeadLetterMiddleware));
        var deserializerPosition = IndexOf(services, typeof(StrictMessageTypeResolver));

        retryPosition
            .Should()
            .BeLessThan(
                deserializerPosition,
                """
                RetryDeadLetterMiddleware must be registered before the deserializer so it wraps it.
                DeserializerConsumerMiddleware has no try/catch, so with the deserializer outermost a malformed payload
                throws past retry/DLQ into KafkaFlow's ConsumerWorker, which logs the exception, swallows it, and
                stores the offset anyway - the message is lost with no retry and no dead letter.
                """
            );
    }

    [Fact]
    public void RegisterTheMetricsMiddlewareInsideTheDeserializerSoOutcomesAreTaggedByEventName()
    {
        var services = new ServiceCollection().AddEventsConsumerKafka(BuildConfiguration());

        var deserializerPosition = IndexOf(services, typeof(StrictMessageTypeResolver));
        var metricsPosition = IndexOf(services, typeof(ConsumerMetricsMiddleware));

        deserializerPosition
            .Should()
            .BeLessThan(
                metricsPosition,
                """
                ConsumerMetricsMiddleware must be registered after the deserializer so it runs inside it. All three of
                its counters are tagged by event_name, and that is only readable off a deserialized payload -
                DeserializerConsumerMiddleware passes the deserialized value inward on a NEW IMessageContext, so a
                middleware outside it sees raw bytes both before and after next returns.
                """
            );
    }

    [Fact]
    public void RegisterTheMetricsMiddlewareOutsideTheDeduplicationMiddlewareSoItOwnsTheUnrecognisedPayloadDecision()
    {
        var services = new ServiceCollection().AddEventsConsumerKafka(BuildConfiguration());

        var metricsPosition = IndexOf(services, typeof(ConsumerMetricsMiddleware));
        var deduplicationPosition = IndexOf(services, typeof(WeatherDeduplicationMiddleware));

        metricsPosition
            .Should()
            .BeLessThan(
                deduplicationPosition,
                """
                ConsumerMetricsMiddleware short-circuits any message whose value is not a BaseEventPayloadDto, which is
                what lets WeatherDeduplicationMiddleware cast the value rather than re-checking it. Reordering these two
                turns that cast into an InvalidCastException on every unrecognised message, which the retry middleware
                would then burn its backoff on before dead lettering.
                """
            );
    }

    [Fact]
    public void ResolveMessageTypesStrictlySoAnUnreadableHeaderCannotBeDroppedSilently()
    {
        var services = new ServiceCollection().AddEventsConsumerKafka(BuildConfiguration());

        services
            .Should()
            .Contain(
                x => x.ServiceType == typeof(StrictMessageTypeResolver),
                """
                The deserializer must use StrictMessageTypeResolver. KafkaFlow's DefaultTypeResolver returns null when
                the Message-Type header is missing or names an unloadable type, and DeserializerConsumerMiddleware
                answers null with a bare return: no handler runs, nothing is logged, no MessageConsumeError event
                fires, and the offset is stored regardless.
                """
            );
    }

    private static int IndexOf(IServiceCollection services, Type serviceType)
    {
        var index = services.ToList().FindIndex(x => x.ServiceType == serviceType);
        index.Should().BeGreaterThanOrEqualTo(0, $"{serviceType.Name} must be registered by AddEventsConsumerKafka");

        return index;
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
