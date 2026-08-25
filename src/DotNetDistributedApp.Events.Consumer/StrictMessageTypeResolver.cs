using KafkaFlow;
using KafkaFlow.Middlewares.Serializer.Resolvers;

namespace DotNetDistributedApp.Events.Consumer;

/// <summary>
/// Resolves an incoming message's CLR type from its <c>Message-Type</c> header, throwing when it cannot.
/// </summary>
/// <remarks>
/// A drop-in replacement for KafkaFlow's internal <c>DefaultTypeResolver</c>, which returns <c>null</c> when the header
/// is missing or names a type this process cannot load. <c>DeserializerConsumerMiddleware</c> answers that <c>null</c>
/// with a bare <c>return</c>: the handlers never run, nothing is logged, no <c>MessageConsumeError</c> global event
/// fires, and the worker stores the offset anyway. The message disappears leaving no trace anywhere — quieter than a
/// malformed payload, which at least throws. Throwing here turns it into an ordinary pipeline failure that
/// <see cref="RetryDeadLetterMiddleware"/>, registered outside the deserializer, routes to the dead letter topic.
/// <para>
/// This is not the "exceptions for control flow" the repo conventions rule out: there is no alternative return path in
/// KafkaFlow's <c>IMessageTypeResolver</c> contract, and an unreadable message is an unrecoverable defect rather than an
/// expected outcome.
/// </para>
/// </remarks>
public class StrictMessageTypeResolver : IMessageTypeResolver
{
    private const string MessageTypeHeader = "Message-Type";

    public ValueTask<Type> OnConsumeAsync(IMessageContext context)
    {
        var typeName = context.Headers.GetString(MessageTypeHeader);

        if (string.IsNullOrWhiteSpace(typeName))
        {
            throw new MessageTypeResolutionException(
                $"Message carries no {MessageTypeHeader} header, so the payload type cannot be resolved."
            );
        }

        // Type.GetType returns null for a type this process cannot load rather than throwing, which is exactly the
        // silent path being closed here. Malformed type names do throw, and those propagate to the DLQ unchanged.
        var messageType =
            Type.GetType(typeName)
            ?? throw new MessageTypeResolutionException(
                $"{MessageTypeHeader} header '{typeName}' does not resolve to a type this process can load."
            );

        return new ValueTask<Type>(messageType);
    }

    /// <summary>
    /// Stamps the header when producing, writing the same value as KafkaFlow's default resolver so a message produced
    /// through either can be consumed through either.
    /// </summary>
    public ValueTask OnProduceAsync(IMessageContext context)
    {
        if (context.Message.Value is null)
        {
            return default;
        }

        var messageType = context.Message.Value.GetType();
        context.Headers.SetString(MessageTypeHeader, $"{messageType.FullName}, {messageType.Assembly.GetName().Name}");

        return default;
    }
}

/// <summary>
/// Thrown when a consumed message's <c>Message-Type</c> header is missing or names an unloadable type. Reaching the dead
/// letter topic with this failure means the message was unreadable, not that a handler rejected it.
/// </summary>
public class MessageTypeResolutionException(string message) : Exception(message);
