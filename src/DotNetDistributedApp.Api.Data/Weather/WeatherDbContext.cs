using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Data.Weather;

public class WeatherDbContext(DbContextOptions<WeatherDbContext> options) : DbContext(options)
{
    public DbSet<WeatherStation> WeatherStations { get; set; }
    public DbSet<WeatherStationHistoricData> WeatherStationHistoricData { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        CreateWeatherStations(modelBuilder);
        CreateWeatherStationHistoricData(modelBuilder);
    }

    private static void CreateWeatherStations(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WeatherStation>();

        entity.HasIndex(x => x.Key).IsUnique();

        entity.HasData(WeatherStationSeedData.GetWeatherStations());
    }

    private static void CreateWeatherStationHistoricData(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WeatherStationHistoricData>();

        entity
            .HasIndex(x => new
            {
                x.WeatherStationId,
                x.Year,
                x.Month,
            })
            .IsDescending(false, true, true);

        entity.HasData(WeatherStationSeedData.GetWeatherStationHistoricDataHeathrow());
        entity.HasData(WeatherStationSeedData.GetWeatherStationHistoricDataStornoway());
    }
}
