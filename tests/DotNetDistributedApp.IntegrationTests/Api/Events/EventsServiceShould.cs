using System.Diagnostics.Metrics;
using AwesomeAssertions;
using AwesomeAssertions.Extensions;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using KafkaFlow;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DotNetDistributedApp.IntegrationTests.Api.Events;

public class EventsServiceShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task SendEvent()
    {
        var expectedPartitionKey = Guid.NewGuid().ToString();
        var expectedValue = "Hello World!";
        var producer = appHostFixture.GetMessageProducer<EventsService>();
        var service = new EventsService(
            producer,
            new MetricsService(appHostFixture.App.Services.GetRequiredService<IMeterFactory>()),
            new NullLogger<EventsService>()
        );

        await service.SendEvent(Topics.Common, new TestMessage(expectedPartitionKey, expectedValue));

        var handler = appHostFixture.GetMessageHandler<TestMessage>();
        await FluentActions
            .Awaiting(() =>
                handler
                    .Received(1)
                    .Handle(
                        Arg.Any<IMessageContext>(),
                        Arg.Is<TestMessage>(x =>
                            x.EventName == "test-message"
                            && x.PartitionKey == expectedPartitionKey
                            && x.Text == expectedValue
                        )
                    )
            )
            .Should()
            .NotThrowAfterAsync(5.Seconds(), 100.Milliseconds());
    }
}
