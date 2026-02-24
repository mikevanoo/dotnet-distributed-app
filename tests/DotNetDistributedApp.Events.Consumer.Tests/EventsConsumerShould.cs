using Confluent.Kafka;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class EventsConsumerShould
{
    private readonly IConsumer<string, BaseEventPayloadDto> _eventConsumer;
    private readonly EventsConsumer _consumer;
    private readonly IEventHandler<SimpleEventPayloadDto> _eventHandler;
    private readonly SimpleEventPayloadDto _simpleEventPayloadDto;
    private readonly FakeLogger<EventsConsumer> _logger;
    private readonly IMetricsService _metricsService;

    public EventsConsumerShould()
    {
        _simpleEventPayloadDto = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "all-good");

        _eventConsumer = Substitute.For<IConsumer<string, BaseEventPayloadDto>>();
        _eventConsumer
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

        var services = new ServiceCollection();
        _eventHandler = Substitute.For<IEventHandler<SimpleEventPayloadDto>>();
        services.AddScoped<IEventHandler<SimpleEventPayloadDto>>(_ => _eventHandler);
        var serviceProvider = services.BuildServiceProvider();

        _metricsService = Substitute.For<IMetricsService>();
        _logger = new FakeLogger<EventsConsumer>();

        _consumer = new EventsConsumer(_eventConsumer, serviceProvider, _metricsService, _logger);
    }

    [Fact]
    public async Task SubscribeToTopic()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);
        _eventConsumer.Received(1).Subscribe(Topics.Common);
    }

    [Fact]
    public async Task ConsumeEvents()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);
        // 2 calls: 1 for the event, 1 for the cancellation
        _eventConsumer.Received(2).Consume(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloseConsumerBeforeStopping()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);
        _eventConsumer.Received(1).Close();
    }

    [Fact]
    public async Task CallHandleForValidEvent()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);
        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HandleUnknownEvent()
    {
        _eventConsumer
            .Consume(Arg.Any<CancellationToken>())
            .Returns(
                // return a single unknown event
                _ => new ConsumeResult<string, BaseEventPayloadDto>
                {
                    Topic = Topics.Common,
                    Message = new Message<string, BaseEventPayloadDto>
                    {
                        Key = Guid.NewGuid().ToString(),
                        Value = new UnknownEventPayloadDto(Guid.NewGuid().ToString()),
                    },
                },
                // simulate cancellation/stopping
                _ => throw new OperationCanceledException()
            );

        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        _logger.ShouldHaveLogged(LogLevel.Warning, "Unrecognised event: unknown-event");
        await _eventHandler.DidNotReceive().HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LogExceptionWhenFailToConsumeEvent()
    {
        // Configure local IEventHandler and EventsConsumer instances to test
        var services = new ServiceCollection();
        var eventHandler = Substitute.For<IEventHandler<SimpleEventPayloadDto>>();
        eventHandler
            .HandleAsync(Arg.Any<SimpleEventPayloadDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Test exception"));
        services.AddScoped<IEventHandler<SimpleEventPayloadDto>>(_ => eventHandler);
        var serviceProvider = services.BuildServiceProvider();
        var consumer = new EventsConsumer(_eventConsumer, serviceProvider, _metricsService, _logger);

        await consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        _logger.ShouldHaveLogged(LogLevel.Error, "Error processing message");
    }
}

public class UnknownEventPayloadDto : BaseEventPayloadDto
{
    public override string EventName => "unknown-event";

    public UnknownEventPayloadDto(string partitionKey)
        : base(partitionKey) { }

    public UnknownEventPayloadDto() { }
}
