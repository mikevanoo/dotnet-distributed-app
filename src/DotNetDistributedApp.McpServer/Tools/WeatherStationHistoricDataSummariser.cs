using DotNetDistributedApp.McpServer.Clients;

namespace DotNetDistributedApp.McpServer.Tools;

public static class WeatherStationHistoricDataSummariser
{
    private const int MonthsPerYear = 12;
    private const int TemperatureDecimals = 2;
    private const int TotalDecimals = 1;

    public static SummarisedWeatherStationHistoricDataDto Summarise(
        IReadOnlyList<Clients.WeatherStationHistoricDataDto> historicData
    )
    {
        if (historicData.Count is 0)
        {
            return new SummarisedWeatherStationHistoricDataDto { Coverage = new HistoricDataCoverageDto() };
        }

        var fromYear = historicData.Min(x => x.Year);
        var toYear = historicData.Max(x => x.Year);
        var maxTemperatureMonths = historicData.Count(x => x.MeanDailyMaxTemperature is not null);
        var minTemperatureMonths = historicData.Count(x => x.MeanDailyMinTemperature is not null);
        var airFrostMonths = historicData.Count(x => x.DaysOfAirFrost is not null);
        var rainfallMonths = historicData.Count(x => x.TotalRainfallMillimeters is not null);
        var sunshineMonths = historicData.Count(x => x.TotalSunshineHours is not null);

        return new SummarisedWeatherStationHistoricDataDto
        {
            FromYear = fromYear,
            ToYear = toYear,
            MeanDailyMaxTemperature = DayWeightedMean(historicData, x => x.MeanDailyMaxTemperature),
            MeanDailyMinTemperature = DayWeightedMean(historicData, x => x.MeanDailyMinTemperature),
            DaysOfAirFrost = airFrostMonths is 0 ? null : historicData.Sum(x => x.DaysOfAirFrost),
            TotalRainfallMillimeters = Round(
                rainfallMonths is 0 ? null : historicData.Sum(x => x.TotalRainfallMillimeters),
                TotalDecimals
            ),
            TotalSunshineHours = Round(
                sunshineMonths is 0 ? null : historicData.Sum(x => x.TotalSunshineHours),
                TotalDecimals
            ),
            Coverage = new HistoricDataCoverageDto
            {
                MonthsExpected = (toYear - fromYear + 1) * MonthsPerYear,
                MonthsReturned = historicData.Count,
                MeanDailyMaxTemperatureMonths = maxTemperatureMonths,
                MeanDailyMinTemperatureMonths = minTemperatureMonths,
                DaysOfAirFrostMonths = airFrostMonths,
                TotalRainfallMonths = rainfallMonths,
                TotalSunshineMonths = sunshineMonths,
                ProvisionalMonths = historicData.Count(x => x.IsProvisional),
            },
        };
    }

    /// <summary>
    /// Each reading is already a mean over one calendar month, so the readings carry unequal weight: 28 to 31 days
    /// each. Averaging them unweighted would give February the same share as July. A month without a reading is
    /// excluded from both the weighted total and the day count, so it neither drags the mean nor inflates it.
    /// </summary>
    private static double? DayWeightedMean(
        IReadOnlyList<Clients.WeatherStationHistoricDataDto> historicData,
        Func<Clients.WeatherStationHistoricDataDto, double?> selector
    )
    {
        var weightedTotal = 0d;
        var totalDays = 0;

        foreach (var month in historicData)
        {
            if (selector(month) is not { } reading || DaysInMonth(month.Year, month.Month) is not { } days)
            {
                continue;
            }

            weightedTotal += reading * days;
            totalDays += days;
        }

        return totalDays is 0 ? null : Round(weightedTotal / totalDays, TemperatureDecimals);
    }

    // A row the API should never produce - a month outside 1 to 12 - carries no usable weight, so it is skipped
    // rather than throwing out of DateTime.DaysInMonth.
    private static int? DaysInMonth(int year, int month) =>
        year is >= 1 and <= 9999 && month is >= 1 and <= MonthsPerYear ? DateTime.DaysInMonth(year, month) : null;

    // Sums and quotients of doubles accumulate artefacts such as 30.299999999999997, which a language model reads
    // back verbatim. The source readings carry one decimal place.
    private static double? Round(double? value, int decimals) =>
        value is { } number ? Math.Round(number, decimals) : null;
}
