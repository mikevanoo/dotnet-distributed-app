using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class EventsConsumerOnFailureShould
{
    private readonly EventsConsumer _consumer;
    private readonly IProducer<string, BaseEventPayloadDto> _eventProducer;
    private readonly IEventHandler<SimpleEventPayloadDto> _eventHandler;
    private readonly SimpleEventPayloadDto _simpleEventPayloadDto;
    private readonly FakeLogger<EventsConsumer> _logger;

    public EventsConsumerOnFailureShould()
    {
        _simpleEventPayloadDto = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "all-good");

        var eventConsumer = Substitute.For<IConsumer<string, BaseEventPayloadDto>>();
        eventConsumer
            .Consume(Arg.Any<CancellationToken>())
            .Returns(
                // return a single event
                _ => new ConsumeResult<string, BaseEventPayloadDto>
                {
                    Topic = Topics.Common,
                    Message = new Message<string, BaseEventPayloadDto>
                    {
                        Key = Guid.NewGuid().ToString(),
                        Value = _simpleEventPayloadDto,
                    },
                },
                // simulate cancellation/stopping
                _ => throw new OperationCanceledException()
            );

        _eventProducer = Substitute.For<IProducer<string, BaseEventPayloadDto>>();

        var services = new ServiceCollection();
        _eventHandler = Substitute.For<IEventHandler<SimpleEventPayloadDto>>();
        _eventHandler
            .HandleAsync(Arg.Any<SimpleEventPayloadDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Test exception"));
        services.AddScoped<IEventHandler<SimpleEventPayloadDto>>(_ => _eventHandler);
        var serviceProvider = services.BuildServiceProvider();

        var metricsService = Substitute.For<IMetricsService>();
        _logger = new FakeLogger<EventsConsumer>();

        _consumer = new EventsConsumer(eventConsumer, _eventProducer, serviceProvider, metricsService, _logger);
    }

    [Fact]
    public async Task LogException()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        _logger.ShouldHaveLogged(LogLevel.Error, "Error processing message");
    }

    [Fact]
    public async Task ProduceEventToOutOfOrderTopic()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        await _eventProducer
            .Received(1)
            .ProduceAsync(
                Topics.OutOfOrder,
                Arg.Any<Message<string, BaseEventPayloadDto>>(),
                TestContext.Current.CancellationToken
            );
    }
}
