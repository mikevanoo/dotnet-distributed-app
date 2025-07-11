using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Weather;

public class WeatherService(WeatherDbContext dbContext)
{
    public async Task<List<WeatherStationDto>> GetWeatherStations()
    {
        var stations = await dbContext.WeatherStations.OrderBy(x => x.Key).ToArrayAsync();
        return stations.Select(station => new WeatherStationDto
        {
            Key = station.Key,
            DisplayName = station.DisplayName,
            Longitude = station.Longitude,
            Latitude = station.Latitude
        })
        .OrderBy(x => x.DisplayName)
        .ToList();
    }
}