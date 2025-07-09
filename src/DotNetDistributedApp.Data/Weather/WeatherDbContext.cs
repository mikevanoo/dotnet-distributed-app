using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Data.Weather;

public class WeatherDbContext(DbContextOptions<WeatherDbContext> options) : DbContext(options)
{
    public DbSet<WeatherStation> WeatherStations { get; set; }

    // protected override void OnModelCreating(ModelBuilder modelBuilder)
    // {
    //     base.OnModelCreating(modelBuilder);
    //     
    //     // CreateWeatherStation(modelBuilder);
    // }
    
    // private static void CreateWeatherStation(ModelBuilder modelBuilder)
    // {
    //     var entity = modelBuilder.Entity<WeatherStation>();
    //
    //     entity.Property(x => x.SubscriptionId).IsRequired(false);
    //     entity.HasIndex(x => x.SubscriptionId).IsUnique();
    //
    //     entity.HasIndex(x => x.EmailAddress).IsUnique();
    //
    //     entity.HasOne(e => e.Subscription).WithOne(e => e.User).IsRequired(false);
    //     entity.Property(x => x.OnBoarded).HasDefaultValue(true);
    //     entity.HasData(UserSeedData.Get());
    // }
}