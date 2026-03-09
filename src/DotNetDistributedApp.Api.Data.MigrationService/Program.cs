using DotNetDistributedApp.Api.Data;
using DotNetDistributedApp.Api.Data.MigrationService;
using DotNetDistributedApp.Api.Data.Weather;

var builder = Host.CreateApplicationBuilder(args);
builder
    .Services.AddHostedService<Worker>()
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

builder.Services.AddApiDatabaseContext(
    builder.Configuration,
    commandTimeoutSeconds: (int)TimeSpan.FromMinutes(5).TotalSeconds
);

var host = builder.Build();
host.Run();
