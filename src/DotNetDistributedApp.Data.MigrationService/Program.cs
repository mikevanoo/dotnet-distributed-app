using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Data.MigrationService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>()
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

builder.Services.AddApiDatabaseContext<WeatherDbContext>(builder.Configuration);

var host = builder.Build();
host.Run();
