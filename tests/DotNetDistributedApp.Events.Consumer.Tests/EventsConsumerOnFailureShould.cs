using Confluent.Kafka;
using DotNetDistributedApp.Api.Common;
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
    private readonly IEventsService<BaseEventPayloadDto> _eventsService;
    private readonly IEventHandler<SimpleEventPayloadDto> _eventHandler;
    private SimpleEventPayloadDto _simpleEventPayloadDto;
    private readonly FakeLogger<EventsConsumer> _logger;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

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

        _eventsService = Substitute.For<IEventsService<BaseEventPayloadDto>>();

        var services = new ServiceCollection();
        _eventHandler = Substitute.For<IEventHandler<SimpleEventPayloadDto>>();
        _eventHandler
            .HandleAsync(Arg.Any<SimpleEventPayloadDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Test exception"));
        services.AddScoped<IEventHandler<SimpleEventPayloadDto>>(_ => _eventHandler);
        var serviceProvider = services.BuildServiceProvider();

        var metricsService = Substitute.For<IMetricsService>();
        _logger = new FakeLogger<EventsConsumer>();
        var dateTimeProvider = Substitute.For<IDateTimeProvider>();
        dateTimeProvider.UtcNow.Returns(_now);

        _consumer = new EventsConsumer(
            eventConsumer,
            _eventsService,
            serviceProvider,
            metricsService,
            dateTimeProvider,
            _logger
        );
    }

    [Fact]
    public async Task LogException()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        _logger.ShouldHaveLogged(
            LogLevel.Error,
            $"Error processing message: {_simpleEventPayloadDto.EventName} (PartitionKey: {_simpleEventPayloadDto.PartitionKey})"
        );
    }

    [Fact]
    public async Task SendEventToOutOfOrderTopic()
    {
        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        await _eventsService
            .Received(1)
            .SendEvent(
                Topics.OutOfOrder,
                Arg.Is<SimpleEventPayloadDto>(x =>
                    x.EventName == _simpleEventPayloadDto.EventName
                    && x.PartitionKey == _simpleEventPayloadDto.PartitionKey
                    && x.Value == _simpleEventPayloadDto.Value
                    && x.Retry.TargetTopic == Topics.Common
                    && x.Retry.FailedCount == 1
                    && x.Retry.FirstFailureTimestamp == _now
                    && x.Retry.LastFailureTimestamp == _now
                )
            );
        _logger.ShouldHaveLogged(
            LogLevel.Information,
            $"Sending event to out-of-order topic: {_simpleEventPayloadDto.EventName} (PartitionKey: {_simpleEventPayloadDto.PartitionKey}, FailedCount: 1)"
        );
    }

    [Fact]
    public async Task SendPreviouslyFailedEventToOutOfOrderTopic()
    {
        var firstFailureTimestamp = DateTimeOffset.UtcNow.AddMinutes(-1);
        _simpleEventPayloadDto = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "all-good")
        {
            Retry = new BaseEventPayloadDto.RetryMetadata
            {
                TargetTopic = Topics.Common,
                FailedCount = 1,
                FirstFailureTimestamp = firstFailureTimestamp,
                LastFailureTimestamp = _now,
            },
        };

        await _consumer.ExecuteAsync(TestContext.Current.CancellationToken);

        await _eventHandler.Received(1).HandleAsync(_simpleEventPayloadDto, TestContext.Current.CancellationToken);
        await _eventsService
            .Received(1)
            .SendEvent(
                Topics.OutOfOrder,
                Arg.Is<SimpleEventPayloadDto>(x =>
                    x.EventName == _simpleEventPayloadDto.EventName
                    && x.PartitionKey == _simpleEventPayloadDto.PartitionKey
                    && x.Value == _simpleEventPayloadDto.Value
                    && x.Retry.TargetTopic == Topics.Common
                    && x.Retry.FailedCount == 2
                    && x.Retry.FirstFailureTimestamp == firstFailureTimestamp
                    && x.Retry.LastFailureTimestamp == _now
                )
            );
        _logger.ShouldHaveLogged(
            LogLevel.Information,
            $"Sending event to out-of-order topic: {_simpleEventPayloadDto.EventName} (PartitionKey: {_simpleEventPayloadDto.PartitionKey}, FailedCount: 2)"
        );
    }
}
