using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetDistributedApp.Api.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApiDatabaseContext<TContext>(
        this IServiceCollection services,
        ConfigurationManager configuration
    )
        where TContext : DbContext =>
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
                            .CommandTimeout((int)TimeSpan.FromMinutes(5).TotalSeconds)
                            .EnableRetryOnFailure()
                )
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention();
        });
}
