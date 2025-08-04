using System.Diagnostics.CodeAnalysis;
using DotNetDistributedApp.Api.Clients;
using DotNetDistributedApp.Api.Common.Errors;
using DotNetDistributedApp.Api.Data.Weather;
using FluentResults;
using Microsoft.EntityFrameworkCore;

namespace DotNetDistributedApp.Api.Weather;

public class WeatherService(WeatherDbContext dbContext, CoordinateConverterClient coordinateConverterClient)
{
    public async Task<Result<List<WeatherStationDto>>> GetWeatherStations()
    {
        var stations = await dbContext.WeatherStations.OrderBy(x => x.DisplayName).ToListAsync();

        var conversionTasks = stations
            .Select(async station =>
            {
                var gridRef = await coordinateConverterClient.ToOsNationalGridReference(
                    station.Latitude,
                    station.Longitude
                );
                if (gridRef.IsSuccess)
                {
                    return new WeatherStationDto
                    {
                        Key = station.Key,
                        DisplayName = station.DisplayName,
                        Longitude = station.Longitude,
                        Latitude = station.Latitude,
                        Easting = gridRef.Value.Easting,
                        Northing = gridRef.Value.Northing,
                    };
                }
                return null;
            })
            .ToList();

        var result = (await Task.WhenAll(conversionTasks)).OfType<WeatherStationDto>().ToList();

        return Result.Ok(result);
    }

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo")]
    [SuppressMessage("Globalization", "CA1311:Specify a culture or use an invariant version")]
    [SuppressMessage(
        "Performance",
        "CA1862:Use the \'StringComparison\' method overloads to perform case-insensitive string comparisons"
    )]
    public async Task<Result<List<WeatherStationHistoricDataDto>>> GetWeatherStationHistoricData(string stationKey)
    {
        var station = await dbContext.WeatherStations.SingleOrDefaultAsync(x =>
            x.Key.ToUpper() == stationKey.ToUpper()
        );
        if (station is null)
        {
            return Result.Fail(new NotFoundError($"Weather Station {stationKey} not found"));
        }

        var historicData = await dbContext
            .WeatherStationHistoricData.Where(x => x.WeatherStationId == station.Id)
            .OrderByDescending(x => x.Year)
            .ThenByDescending(x => x.Month)
            .ToListAsync();

        var result = historicData
            .Select(data => new WeatherStationHistoricDataDto
            {
                Year = data.Year,
                Month = data.Month,
                MeanDailyMaxTemperature = data.MeanDailyMaxTemperature,
                MeanDailyMinTemperature = data.MeanDailyMinTemperature,
                DaysOfAirFrost = data.DaysOfAirFrost,
                TotalRainfallMillimeters = data.TotalRainfallMillimeters,
                TotalSunshineHours = data.TotalSunshineHours,
                IsProvisional = data.IsProvisional,
            })
            .ToList();

        return Result.Ok(result);
    }
}
