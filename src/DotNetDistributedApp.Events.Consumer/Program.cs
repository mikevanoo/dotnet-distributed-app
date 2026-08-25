using System.Globalization;
using DotNetDistributedApp.Api.Common.Events;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Events.Consumer;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.AddServiceDefaults(MetricsService.MeterName);
    builder
        .Services.AddSerilog(config => config.ReadFrom.Configuration(builder.Configuration))
        .AddApiDatabaseContext(builder.Configuration)
        .AddSingleton<IMetricsService, MetricsService>()
        .AddSingleton<IEventsService, EventsService>()
        .Configure<RetryDeadLetterOptions>(builder.Configuration.GetSection("RetryDeadLetter"))
        .AddEventsConsumerKafka(builder.Configuration);

    var app = builder.Build();
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
