using System.Globalization;
using Coravel;
using DotNetDistributedApp.Api.Common.Metrics;
using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.ScheduledTasks.ProcessedWeatherEvents;
using DotNetDistributedApp.ServiceDefaults;
using Microsoft.Extensions.Options;
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
        .AddTransient<ProcessedWeatherEventsCleaner>()
        .AddScheduler();

    builder
        .Services.AddOptions<ProcessedWeatherEventsCleanerOptions>()
        .Bind(builder.Configuration.GetSection("ProcessedWeatherEventsCleaner"))
        .Validate(
            options =>
                options.DefaultRetention > TimeSpan.Zero
                && options.RetentionByEventName.Values.All(retention => retention > TimeSpan.Zero)
                && !string.IsNullOrWhiteSpace(options.Cron),
            "Retention periods must be greater than zero and Cron must not be empty."
        )
        .ValidateOnStart();

    var app = builder.Build();

    var cleanerOptions = app.Services.GetRequiredService<IOptions<ProcessedWeatherEventsCleanerOptions>>().Value;
    app.Services.UseScheduler(scheduler =>
        {
            scheduler
                .Schedule<ProcessedWeatherEventsCleaner>()
                .Cron(cleanerOptions.Cron)
                .PreventOverlapping(nameof(ProcessedWeatherEventsCleaner));
        })
        .OnError(exception => Log.Error(exception, "ProcessedWeatherEventsCleaner failed"));
    ;

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
