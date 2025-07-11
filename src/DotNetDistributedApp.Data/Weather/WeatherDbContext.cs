using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Data.Weather;

public class WeatherDbContext(DbContextOptions<WeatherDbContext> options) : DbContext(options)
{
    public DbSet<WeatherStation> WeatherStations { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        CreateWeatherStations(modelBuilder);
    }
    
    private static void CreateWeatherStations(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<WeatherStation>();
    
        entity.HasIndex(x => x.Key).IsUnique();
    
        entity.HasData(WeatherStationSeedData.Get());
    }
}