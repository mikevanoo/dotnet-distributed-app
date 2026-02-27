using AwesomeAssertions;
using DotNetDistributedApp.Api.Common.Events;
using KafkaFlow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace DotNetDistributedApp.Events.Consumer.Tests;

public class FailingEventMessageHandlerShould
{
    [Fact]
    public async Task LogPayload()
    {
        var messageContext = Substitute.For<IMessageContext>();
        var payload = new FailingEventPayloadDto(Guid.NewGuid().ToString());
        var logger = new FakeLogger<FailingEventMessageHandler>();
        var handler = new FailingEventMessageHandler(logger);

        var act = () => handler.Handle(messageContext, payload);

        await act.Should().ThrowExactlyAsync<ArgumentException>().WithMessage("Simulated exception");
        logger.ShouldHaveLogged(LogLevel.Information, $"Handling failing event: failing-event {payload.PartitionKey}");
    }
}
