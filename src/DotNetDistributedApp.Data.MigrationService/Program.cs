using DotNetDistributedApp.Api.Data.Weather;
using DotNetDistributedApp.Data.MigrationService;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>()
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(Worker.ActivitySourceName));

builder.Services.AddDbContextPool<WeatherDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("api-database")
                           ?? throw new InvalidOperationException("Connection string 'api-database' not found.");
    options
        .UseNpgsql(
            connectionString,
            x => x
                .MigrationsHistoryTable("__efmigrationshistory", "public")
                .CommandTimeout((int)TimeSpan.FromMinutes(5).TotalSeconds)
            )
        .UseNpgsql(connectionString)
        .UseSnakeCaseNamingConvention();
});

var host = builder.Build();
host.Run();
