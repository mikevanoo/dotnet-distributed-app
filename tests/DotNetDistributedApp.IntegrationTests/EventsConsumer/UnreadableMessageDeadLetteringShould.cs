using System.Text;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;

namespace DotNetDistributedApp.IntegrationTests.EventsConsumer;

/*
 * A message the consumer cannot deserialize used to disappear: DeserializerConsumerMiddleware has no try/catch, so a
 * malformed payload threw straight past retry/DLQ into KafkaFlow's ConsumerWorker, which logged it, swallowed it, and
 * stored the offset anyway. A missing or unloadable Message-Type header was quieter still - the deserializer answered
 * DefaultTypeResolver's null with a bare return, so there was no exception and no log at all.
 *
 * Both are now dead lettered instead, and these tests are the only thing that proves it end to end. They need a
 * producer with no serializer (bytes on the topic verbatim) and an observer on common-dlq that reads raw bytes, because
 * a poison dead letter cannot reach a typed handler either. Both live on AppHostFixture.
 */
public class UnreadableMessageDeadLetteringShould(AppHostFixture appHostFixture)
{
    private const string MessageTypeHeader = "Message-Type";

    [Fact]
    public async Task DeadLetterAMessageWithNoMessageTypeHeader()
    {
        using var deadline = AppHostFixture.CreateDeadline();
        var payload = $$"""{"marker":"{{Guid.NewGuid()}}"}""";

        await ProduceRawEvent(payload, new MessageHeaders());

        var deadLetterHeaders = await appHostFixture.WaitForUnreadableDeadLetter(payload, deadline.Token);

        deadLetterHeaders
            .GetString(MessageTypeHeader)
            .Should()
            .BeNull(
                "the dead letter must be the original message forwarded as-is - a Message-Type appearing from nowhere "
                    + "would mean something re-serialized it on the way to the DLQ"
            );
    }

    [Fact]
    public async Task DeadLetterAMessageWhosePayloadIsMalformed()
    {
        using var deadline = AppHostFixture.CreateDeadline();
        // A valid Message-Type naming a type the consumer can load, so it is the JSON that fails, not the type lookup.
        var messageType =
            $"{typeof(SimpleEventPayloadDto).FullName}, {typeof(SimpleEventPayloadDto).Assembly.GetName().Name}";
        var payload = $"this is not json {Guid.NewGuid()}";
        var headers = new MessageHeaders();
        headers.SetString(MessageTypeHeader, messageType);

        await ProduceRawEvent(payload, headers);

        var deadLetterHeaders = await appHostFixture.WaitForUnreadableDeadLetter(payload, deadline.Token);

        deadLetterHeaders
            .GetString(MessageTypeHeader)
            .Should()
            .Be(
                messageType,
                "the original headers must be carried over, or a dead letter that only failed on its body stops being "
                    + "deserializable by anything reading the DLQ"
            );
    }

    private async Task ProduceRawEvent(string payload, IMessageHeaders headers) =>
        await appHostFixture
            .GetMessageProducer<UnreadableEventProducer>()
            .ProduceAsync(Topics.Common, Guid.NewGuid().ToString(), Encoding.UTF8.GetBytes(payload), headers);
}
