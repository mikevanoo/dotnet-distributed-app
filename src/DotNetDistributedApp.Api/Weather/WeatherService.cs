using DotNetDistributedApp.Api.Data.Weather;
using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Weather;

public class WeatherService(WeatherDbContext dbContext)
{
    public async Task<List<WeatherStationDto>> GetWeatherStations()
    {
        var stations = await dbContext.WeatherStations.OrderBy(x => x.DisplayName).ToListAsync();
        
        return stations.Select(station => new WeatherStationDto
        {
            Key = station.Key,
            DisplayName = station.DisplayName,
            Longitude = station.Longitude,
            Latitude = station.Latitude
        })
        .ToList();
    }
    
    public async Task<List<WeatherStationHistoricDataDto>> GetWeatherStationHistoricData(string stationKey)
    {
        var station = await dbContext.WeatherStations.SingleOrDefaultAsync(x => x.Key.ToUpper() == stationKey.ToUpper());
        if (station == null)
        {
            return null;
        }
        
        var historicData = await dbContext.WeatherStationHistoricData
            .Where(x => x.WeatherStationId == station.Id)
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ToListAsync();
        
        return historicData.Select(data => new WeatherStationHistoricDataDto
            {
                Year = data.Year,
                Month = data.Month,
                MeanDailyMaxTemperature = data.MeanDailyMaxTemperature,
                MeanDailyMinTemperature = data.MeanDailyMinTemperature,
                DaysOfAirFrost = data.DaysOfAirFrost,
                TotalRainfallMillimeters = data.TotalRainfallMillimeters,
                TotalSunshineHours = data.TotalSunshineHours,
                IsProvisional = data.IsProvisional
            })
            .ToList();
    }
}