using DotNetDistributedApp.Api.Common.Events;

namespace DotNetDistributedApp.IntegrationTests.Api.Events;

public class TestMessage(string partitionKey, string text) : BaseEventPayloadDto(partitionKey)
{
    public override string EventName => "test-message";

    public string Text { get; set; } = text;
}
