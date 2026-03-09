using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetDistributedApp.Api.Data;

public static class ServiceCollectionExtensions
{
    private static readonly int DefaultCommandTimeoutSeconds = (int)TimeSpan.FromSeconds(30).TotalSeconds;

    public static IServiceCollection AddApiDatabaseContext(
        this IServiceCollection services,
        ConfigurationManager configuration,
        int? commandTimeoutSeconds = null
    ) =>
        services.AddDbContextPool<WeatherDbContext>(options =>
        {
            var connectionString =
                configuration.GetConnectionString("api-database")
                ?? throw new InvalidOperationException("Connection string 'api-database' not found.");
            options
                .UseNpgsql(
                    connectionString,
                    x =>
                        x.MigrationsHistoryTable("__efmigrationshistory", "public")
                            .CommandTimeout(commandTimeoutSeconds ?? DefaultCommandTimeoutSeconds)
                            .EnableRetryOnFailure()
                )
                .UseSnakeCaseNamingConvention();
        });
}
