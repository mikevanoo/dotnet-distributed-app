using KafkaFlow;

namespace DotNetDistributedApp.IntegrationTests.Api.Events;

public class DelegatingTestMessageHandler(IMessageHandler<TestMessage> inner) : IMessageHandler<TestMessage>
{
    public Task Handle(IMessageContext context, TestMessage message) => inner.Handle(context, message);
}
