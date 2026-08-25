using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Events.Consumer;
using KafkaFlow;

namespace DotNetDistributedApp.IntegrationTests.EventsConsumer;

/// <summary>
/// The <c>common-dlq</c> half of <see cref="DeadLetterRecorder"/>. Registered on the fixture's dead letter consumer,
/// never on the pipeline under test.
/// </summary>
/// <remarks>
/// Only <see cref="FailingEventPayloadDto"/> is handled, because it is the only payload whose handler throws and so
/// the only one that is ever dead lettered. A payload type with no handler is harmless — KafkaFlow's
/// <c>TypedHandlerMiddleware</c> just calls <c>next</c> — so add a handler per payload type if that changes.
/// </remarks>
public class DeadLetteredFailingEventMessageHandler(DeadLetterRecorder deadLetterRecorder)
    : IMessageHandler<FailingEventPayloadDto>
{
    public Task Handle(IMessageContext context, FailingEventPayloadDto message)
    {
        deadLetterRecorder.Record(
            context.Headers.GetString(DeadLetterHeaders.ConsumerGroup) ?? string.Empty,
            message.EventId
        );

        return Task.CompletedTask;
    }
}
