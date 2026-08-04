using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DotNetDistributedApp.Events.Consumer.Tests;

/*
 * The middleware is the outermost one in the pipeline, outside the deserializer, so every context here holds RAW
 * Kafka bytes and a raw key - never a deserialized payload. That is not a simplification for the tests; it is what
 * DeserializerConsumerMiddleware guarantees by passing the deserialized value inward on a new IMessageContext.
 */
public class RetryDeadLetterMiddlewareShould
{
    private const string ConsumerGroup = "some-consumer-group";
    private const string MessageTypeHeader = "Message-Type";

    [Fact]
    public async Task SendMessageToTheDeadLetterTopicOnceRetriesAreExhausted()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        var middleware = CreateMiddleware(dlqProducer, maxRetryCount: 2);

        await middleware.Invoke(CreateMessageContext(payload), _ => throw new ArgumentException("Simulated exception"));

        await dlqProducer
            .Received(1)
            .ProduceAsync(
                Topics.CommonDlq,
                Arg.Is<object>(key => KeyAsString(key) == payload.PartitionKey),
                Arg.Is<object>(value => ValueAsString(value) == Serialize(payload)),
                Arg.Any<IMessageHeaders>()
            );
    }

    [Fact]
    public async Task ForwardTheOriginalHeadersSoTheDeadLetterStaysDeserializable()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        var middleware = CreateMiddleware(dlqProducer);

        await middleware.Invoke(CreateMessageContext(payload), _ => throw new ArgumentException("Simulated exception"));

        await dlqProducer
            .Received(1)
            .ProduceAsync(
                Topics.CommonDlq,
                Arg.Any<object>(),
                Arg.Any<object>(),
                Arg.Is<IMessageHeaders>(headers =>
                    headers.GetString(MessageTypeHeader) == MessageTypeHeaderValue(payload)
                )
            );
    }

    [Fact]
    public async Task TagDeadLetteredMessagesWithTheConsumerGroupThatGaveUp()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        var middleware = CreateMiddleware(dlqProducer);

        await middleware.Invoke(CreateMessageContext(payload), _ => throw new ArgumentException("Simulated exception"));

        await dlqProducer
            .Received(1)
            .ProduceAsync(
                Topics.CommonDlq,
                Arg.Any<object>(),
                Arg.Any<object>(),
                Arg.Is<IMessageHeaders>(x => x.GetString(DeadLetterHeaders.ConsumerGroup) == ConsumerGroup)
            );
    }

    [Fact]
    public async Task NotSendMessageToTheDeadLetterTopicWhenHandlingSucceeds()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        var middleware = CreateMiddleware(dlqProducer);

        await middleware.Invoke(CreateMessageContext(payload), _ => Task.CompletedTask);

        await dlqProducer
            .DidNotReceive()
            .ProduceAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<object>(), Arg.Any<IMessageHeaders>());
    }

    [Fact]
    public async Task KeepTheOffsetUncommittedWhenTheDeadLetterProduceFails()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        dlqProducer
            .ProduceAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<object>(), Arg.Any<IMessageHeaders>())
            .ThrowsAsync(new InvalidOperationException("Simulated dead letter topic outage"));
        var context = CreateMessageContext(payload);
        var middleware = CreateMiddleware(dlqProducer);

        await FluentActions
            .Awaiting(() => middleware.Invoke(context, _ => throw new ArgumentException("Simulated exception")))
            .Should()
            .ThrowAsync<InvalidOperationException>();

        context
            .ConsumerContext.ShouldStoreOffset.Should()
            .BeFalse(
                """
                Rethrowing is not enough. KafkaFlow's ConsumerWorker.ProcessMessageAsync catches every
                non-cancellation exception and its finally block still calls ConsumerContext.Complete(), which
                stores the offset. Only ShouldStoreOffset = false keeps a message the dead letter topic refused
                available for redelivery instead of losing it.
                """
            );
    }

    [Fact]
    public async Task StoreTheOffsetWhenTheDeadLetterProduceSucceeds()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var context = CreateMessageContext(payload);
        var middleware = CreateMiddleware(Substitute.For<IMessageProducer<DlqProducer>>());

        await middleware.Invoke(context, _ => throw new ArgumentException("Simulated exception"));

        context
            .ConsumerContext.ShouldStoreOffset.Should()
            .BeTrue("a message safely parked on the dead letter topic must not be redelivered");
    }

    private static RetryDeadLetterMiddleware CreateMiddleware(
        IMessageProducer<DlqProducer> dlqProducer,
        int maxRetryCount = 0
    ) =>
        new(
            dlqProducer,
            Options.Create(
                new RetryDeadLetterOptions
                {
                    MaxRetryCount = maxRetryCount,
                    RetryDelay = TimeSpan.Zero,
                    UseExponentialBackoff = false,
                }
            ),
            new FakeLogger<RetryDeadLetterMiddleware>()
        );

    /// <summary>
    /// A context shaped the way <c>ConsumerWorkerPool.CreateMessageContext</c> shapes one: byte array key, byte array
    /// value, and the headers the producer stamped — including <c>Message-Type</c>.
    /// </summary>
    private static IMessageContext CreateMessageContext(BaseEventPayloadDto payload)
    {
        var consumerContext = Substitute.For<IConsumerContext>();
        consumerContext.GroupId.Returns(ConsumerGroup);
        // KafkaFlow's ConsumerContext defaults this to true, and a substitute's bool defaults to false - without
        // seeding it, a test asserting it became false would pass whether or not anything set it.
        consumerContext.ShouldStoreOffset = true;

        var headers = new MessageHeaders();
        headers.SetString(MessageTypeHeader, MessageTypeHeaderValue(payload));

        var messageContext = Substitute.For<IMessageContext>();
        messageContext.Message.Returns(
            new Message(Encoding.UTF8.GetBytes(payload.PartitionKey), Encoding.UTF8.GetBytes(Serialize(payload)))
        );
        messageContext.Headers.Returns(headers);
        messageContext.ConsumerContext.Returns(consumerContext);

        return messageContext;
    }

    private static string MessageTypeHeaderValue(BaseEventPayloadDto payload) =>
        $"{payload.GetType().FullName}, {payload.GetType().Assembly.GetName().Name}";

    private static string Serialize(BaseEventPayloadDto payload) =>
        JsonSerializer.Serialize(payload, payload.GetType());

    private static string? KeyAsString(object? key) => key is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;

    private static string? ValueAsString(object? value) =>
        value is byte[] bytes ? Encoding.UTF8.GetString(bytes) : null;
}
