using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class RetryDeadLetterMiddlewareShould
{
    private const string ConsumerGroup = "some-consumer-group";

    [Fact]
    public async Task SendMessageToTheDeadLetterTopicOnceRetriesAreExhausted()
    {
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var dlqProducer = Substitute.For<IMessageProducer<DlqProducer>>();
        var middleware = CreateMiddleware(dlqProducer, maxRetryCount: 2);

        await middleware.Invoke(CreateMessageContext(payload), _ => throw new ArgumentException("Simulated exception"));

        await dlqProducer
            .Received(1)
            .ProduceAsync(Topics.CommonDlq, payload.PartitionKey, payload, Arg.Any<IMessageHeaders>());
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
                payload.PartitionKey,
                payload,
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
            .ProduceAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<IMessageHeaders>());
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

    private static IMessageContext CreateMessageContext(BaseEventPayloadDto payload)
    {
        var consumerContext = Substitute.For<IConsumerContext>();
        consumerContext.GroupId.Returns(ConsumerGroup);

        var messageContext = Substitute.For<IMessageContext>();
        messageContext.Message.Returns(new Message(payload.PartitionKey, payload));
        messageContext.ConsumerContext.Returns(consumerContext);

        return messageContext;
    }
}
