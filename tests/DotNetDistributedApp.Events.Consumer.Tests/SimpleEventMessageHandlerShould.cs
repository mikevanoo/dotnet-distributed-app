using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class SimpleEventMessageHandlerShould
{
    [Fact]
    public async Task LogPayload()
    {
        var messageContext = Substitute.For<IMessageContext>();
        var payload = new SimpleEventPayloadDto(Guid.NewGuid().ToString(), "all-good");
        var logger = new FakeLogger<SimpleEventMessageHandler>();
        var handler = new SimpleEventMessageHandler(logger);

        await handler.Handle(messageContext, payload);

        logger.ShouldHaveLogged(
            LogLevel.Information,
            $"Handling simple event: {{\"EventName\":\"simple-event\",\"Value\":\"{payload.Value}\",\"PartitionKey\":\"{payload.PartitionKey}\"}}"
        );
    }
}
