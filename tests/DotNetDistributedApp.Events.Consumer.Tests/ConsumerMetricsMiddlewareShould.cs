using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using KafkaFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace DotNetDistributedApp.Events.Consumer.Tests;

/*
 * The middleware sits INSIDE the deserializer, so every context here holds a deserialized payload rather than raw
 * bytes - the opposite of RetryDeadLetterMiddlewareShould. That is the whole reason for its position: EventName, which
 * all three counters are tagged by, does not exist until the deserializer has run.
 */
public class ConsumerMetricsMiddlewareShould
{
    private const long Offset = 42;

    private readonly IMetricsService _metricsService = Substitute.For<IMetricsService>();
    private readonly FakeLogger<ConsumerMetricsMiddleware> _logger = new();

    [Fact]
    public async Task CountAConsumeThatCompletedAsASuccess()
    {
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "some-value");

        await CreateMiddleware().Invoke(CreateMessageContext(payload), _ => Task.CompletedTask);

        _metricsService.Received(1).ConsumeEventSuccess(1, Topics.Common, payload.EventName);
        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventFailed(default, default!, default!);
        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventUnrecognised(default, default!, default!);
    }

    [Fact]
    public async Task CountAConsumeThatThrewAsAFailureAndRethrowSoRetryAndDeadLetteringStillHappen()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var middleware = CreateMiddleware();

        await FluentActions
            .Awaiting(() =>
                middleware.Invoke(
                    CreateMessageContext(payload),
                    _ => throw new ArgumentException("Simulated exception")
                )
            )
            .Should()
            .ThrowAsync<ArgumentException>(
                """
                RetryDeadLetterMiddleware is outside this middleware, so swallowing the exception here would cost
                the message its retries and its dead letter.
                """
            );

        _metricsService.Received(1).ConsumeEventFailed(1, Topics.Common, payload.EventName);
        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventSuccess(default, default!, default!);
    }

    // events.consume_failed matches LogMessageHandlingFailed's per-attempt granularity: the retry middleware
    // re-invokes this middleware once per attempt, so two attempts must count twice.
    [Fact]
    public async Task CountAFailurePerAttemptRatherThanPerMessage()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var context = CreateMessageContext(payload);
        var middleware = CreateMiddleware();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await FluentActions
                .Awaiting(() => middleware.Invoke(context, _ => throw new ArgumentException("Simulated exception")))
                .Should()
                .ThrowAsync<ArgumentException>();
        }

        _metricsService.Received(2).ConsumeEventFailed(1, Topics.Common, payload.EventName);
    }

    // A shutdown is not a consume failure - counting it would make every deployment look like an outage.
    // RetryDeadLetterMiddleware makes the same carve-out, for the same reason.
    [Fact]
    public async Task NotCountAFailureWhenTheWorkerIsStopping()
    {
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "some-value");
        var middleware = CreateMiddleware();

        await FluentActions
            .Awaiting(() =>
                middleware.Invoke(
                    CreateMessageContext(payload, workerStopped: true),
                    _ => throw new OperationCanceledException()
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();

        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventFailed(default, default!, default!);
        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventSuccess(default, default!, default!);
    }

    // Only a cancellation caused by the worker stopping is exempt. A handler that times out its own database call is
    // a genuine failure, and the retry middleware treats it as one too.
    [Fact]
    public async Task CountAFailureWhenAHandlerThrowsCancellationWhileTheWorkerIsRunning()
    {
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "some-value");
        var middleware = CreateMiddleware();

        await FluentActions
            .Awaiting(() =>
                middleware.Invoke(
                    CreateMessageContext(payload, workerStopped: false),
                    _ => throw new OperationCanceledException()
                )
            )
            .Should()
            .ThrowAsync<OperationCanceledException>();

        _metricsService.Received(1).ConsumeEventFailed(1, Topics.Common, payload.EventName);
    }

    [Fact]
    public async Task CountAMessageThatIsNotAnEventPayloadAsUnrecognisedAndSkipTheHandlers()
    {
        var handlersCalled = false;

        await CreateMiddleware()
            .Invoke(
                CreateMessageContext("a string is not a BaseEventPayloadDto"),
                _ =>
                {
                    handlersCalled = true;
                    return Task.CompletedTask;
                }
            );

        handlersCalled.Should().BeFalse("there is nothing downstream that can do anything with an unknown payload");
        _metricsService.Received(1).ConsumeEventUnrecognised(1, Topics.Common, nameof(String));
        _metricsService.DidNotReceiveWithAnyArgs().ConsumeEventSuccess(default, default!, default!);
        _logger.ShouldHaveLogged(
            LogLevel.Warning,
            $"Unrecognised message skipped: {typeof(string).FullName} at offset {Offset}"
        );
    }

    [Fact]
    public async Task CountAMessageWithNoValueAsUnrecognised()
    {
        await CreateMiddleware().Invoke(CreateMessageContext(value: null), _ => Task.CompletedTask);

        _metricsService.Received(1).ConsumeEventUnrecognised(1, Topics.Common, "unknown");
    }

    private ConsumerMetricsMiddleware CreateMiddleware() => new(_metricsService, _logger);

    /// <summary>
    /// A context shaped the way <c>DeserializerConsumerMiddleware</c> shapes the one it passes inward: the message
    /// value is the deserialized object, not the bytes it arrived as.
    /// </summary>
    private static IMessageContext CreateMessageContext(object? value, bool workerStopped = false)
    {
        var consumerContext = Substitute.For<IConsumerContext>();
        consumerContext.Topic.Returns(Topics.Common);
        consumerContext.Offset.Returns(Offset);
        consumerContext.WorkerStopped.Returns(new CancellationToken(workerStopped));

        var messageContext = Substitute.For<IMessageContext>();
        messageContext.Message.Returns(new Message("some-partition-key", value));
        messageContext.ConsumerContext.Returns(consumerContext);

        return messageContext;
    }
}
