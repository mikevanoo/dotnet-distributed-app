using Microsoft.Extensions.Hosting;

namespace DotNetDistributedApp.Events.Consumer;

// TODO remove
public class EventsConsumerHostedService(EventsConsumer eventsConsumer) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await eventsConsumer.ExecuteAsync(stoppingToken);
}
