using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Weather;

public class WeatherService(WeatherDbContext dbContext)
{
    public async Task<List<WeatherStationDto>> GetWeatherStations()
    {
        var stations = await dbContext.WeatherStations.OrderBy(x => x.Name).ToArrayAsync();
        return stations.Select(station => new WeatherStationDto
        {
            Id = station.Id,
            Name = station.Name,
            Longitude = station.Longitude,
            Latitude = station.Latitude
        })
        .ToList();
    }
}