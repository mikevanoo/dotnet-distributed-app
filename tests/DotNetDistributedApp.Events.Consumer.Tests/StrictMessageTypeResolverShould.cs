using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using NSubstitute;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class StrictMessageTypeResolverShould
{
    private const string MessageTypeHeader = "Message-Type";

    [Fact]
    public async Task ResolveThePayloadTypeNamedByTheHeader()
    {
        var resolver = new StrictMessageTypeResolver();

        var messageType = await resolver.OnConsumeAsync(
            CreateMessageContext(
                $"{typeof(SimpleEventPayloadDto).FullName}, {typeof(SimpleEventPayloadDto).Assembly.GetName().Name}"
            )
        );

        messageType.Should().Be<SimpleEventPayloadDto>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ThrowWhenTheMessageTypeHeaderIsMissing(string? headerValue)
    {
        var resolver = new StrictMessageTypeResolver();

        await FluentActions
            .Awaiting(async () => await resolver.OnConsumeAsync(CreateMessageContext(headerValue)))
            .Should()
            .ThrowAsync<MessageTypeResolutionException>(
                """
                KafkaFlow's DefaultTypeResolver returns null here, and DeserializerConsumerMiddleware answers null by
                returning without calling next - no handler, no exception, no log, offset stored anyway. Throwing is
                what lets RetryDeadLetterMiddleware dead letter the message instead.
                """
            );
    }

    [Fact]
    public async Task ThrowWhenTheMessageTypeHeaderNamesATypeThisProcessCannotLoad()
    {
        var resolver = new StrictMessageTypeResolver();

        await FluentActions
            .Awaiting(async () =>
                await resolver.OnConsumeAsync(
                    CreateMessageContext("Some.Removed.Namespace.OldPayloadDto, Some.Assembly")
                )
            )
            .Should()
            .ThrowAsync<MessageTypeResolutionException>("a renamed or deleted payload type must not vanish silently");
    }

    [Fact]
    public async Task StampTheHeaderKafkaFlowsDefaultResolverWouldWriteWhenProducing()
    {
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "some-value");
        var context = CreateMessageContext(headerValue: null, payload);

        await new StrictMessageTypeResolver().OnProduceAsync(context);

        context
            .Headers.GetString(MessageTypeHeader)
            .Should()
            .Be($"{typeof(SimpleEventPayloadDto).FullName}, {typeof(SimpleEventPayloadDto).Assembly.GetName().Name}");
    }

    private static IMessageContext CreateMessageContext(string? headerValue, object? messageValue = null)
    {
        var headers = new MessageHeaders();
        if (headerValue is not null)
        {
            headers.SetString(MessageTypeHeader, headerValue);
        }

        var messageContext = Substitute.For<IMessageContext>();
        messageContext.Headers.Returns(headers);
        messageContext.Message.Returns(new Message("some-key", messageValue));

        return messageContext;
    }
}
