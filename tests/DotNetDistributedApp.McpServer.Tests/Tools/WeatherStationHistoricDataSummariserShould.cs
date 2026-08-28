using AwesomeAssertions;
using DotNetDistributedApp.McpServer.Tools;
using MonthlyReading = DotNetDistributedApp.McpServer.Clients.WeatherStationHistoricDataDto;

namespace DotNetDistributedApp.McpServer.Tests.Tools;

public class WeatherStationHistoricDataSummariserShould
{
    private const double Tolerance = 0.001;

    [Fact]
    public void WeightMeanTemperaturesByTheNumberOfDaysInEachMonth()
    {
        var historicData = new[]
        {
            Month(2021, 1, maxTemperature: 0, minTemperature: 0),
            Month(2021, 2, maxTemperature: 10, minTemperature: 10),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        // (0 x 31 + 10 x 28) / 59 = 4.75, where an unweighted average would give 5.00
        actual.MeanDailyMaxTemperature.Should().BeApproximately(4.75, Tolerance);
        actual.MeanDailyMinTemperature.Should().BeApproximately(4.75, Tolerance);
    }

    [Fact]
    public void GiveFebruaryAnExtraDayOfWeightInALeapYear()
    {
        var historicData = new[] { Month(2020, 1, maxTemperature: 0), Month(2020, 2, maxTemperature: 10) };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        // (0 x 31 + 10 x 29) / 60 = 4.83, against 4.75 for the same months in a non-leap year
        actual.MeanDailyMaxTemperature.Should().BeApproximately(4.83, Tolerance);
    }

    [Fact]
    public void ExcludeAMonthWithoutAReadingFromBothTheWeightedTotalAndTheDayCount()
    {
        var historicData = new[]
        {
            Month(2021, 1, maxTemperature: 0),
            Month(2021, 2, maxTemperature: 10),
            Month(2021, 3, maxTemperature: null),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.MeanDailyMaxTemperature.Should().BeApproximately(4.75, Tolerance);
        actual.Coverage.MeanDailyMaxTemperatureMonths.Should().Be(2);
        actual.Coverage.MonthsReturned.Should().Be(3);
    }

    [Fact]
    public void SkipAMonthOutsideTheCalendarRatherThanThrow()
    {
        var historicData = new[] { Month(2021, 1, maxTemperature: 5), Month(2021, 13, maxTemperature: 100) };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.MeanDailyMaxTemperature.Should().BeApproximately(5, Tolerance);
    }

    [Fact]
    public void TotalTheDaysOfAirFrostRatherThanCountingTheMonthsThatHadFrost()
    {
        var historicData = new[]
        {
            Month(2021, 1, daysOfAirFrost: 5),
            Month(2021, 2, daysOfAirFrost: 0),
            Month(2021, 3, daysOfAirFrost: 3),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.DaysOfAirFrost.Should().Be(8);
        actual.Coverage.DaysOfAirFrostMonths.Should().Be(3);
    }

    [Fact]
    public void TotalRainfallAndSunshineToThePrecisionOfTheSourceReadings()
    {
        var historicData = new[]
        {
            Month(2021, 1, rainfall: 10.1, sunshine: 40.4),
            Month(2021, 2, rainfall: 20.2, sunshine: 50.5),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.TotalRainfallMillimeters.Should().Be(30.3);
        actual.TotalSunshineHours.Should().Be(90.9);
    }

    [Fact]
    public void ReportHowManyMonthsFedEachTotalSoAPartialTotalIsVisible()
    {
        var historicData = new[]
        {
            Month(2021, 1, rainfall: 10.1, sunshine: null),
            Month(2021, 2, rainfall: null, sunshine: 50.5),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.TotalRainfallMillimeters.Should().Be(10.1);
        actual.Coverage.TotalRainfallMonths.Should().Be(1);
        actual.TotalSunshineHours.Should().Be(50.5);
        actual.Coverage.TotalSunshineMonths.Should().Be(1);
    }

    [Fact]
    public void ReturnNullRatherThanZeroWhenNoMonthCarriesAReading()
    {
        var historicData = new[] { Month(2021, 1), Month(2021, 2) };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.MeanDailyMaxTemperature.Should().BeNull();
        actual.MeanDailyMinTemperature.Should().BeNull();
        actual.DaysOfAirFrost.Should().BeNull();
        actual.TotalRainfallMillimeters.Should().BeNull();
        actual.TotalSunshineHours.Should().BeNull();
        actual.Coverage.MonthsReturned.Should().Be(2);
    }

    [Fact]
    public void ReportTheYearsPresentInTheDataAndTheMonthsThoseYearsShouldHold()
    {
        var historicData = new[] { Month(2019, 6), Month(2021, 7) };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.FromYear.Should().Be(2019);
        actual.ToYear.Should().Be(2021);
        actual.Coverage.MonthsExpected.Should().Be(36);
        actual.Coverage.MonthsReturned.Should().Be(2);
    }

    [Fact]
    public void CountTheMonthsWhoseReadingsAreStillProvisional()
    {
        var historicData = new[]
        {
            Month(2021, 1, isProvisional: true),
            Month(2021, 2, isProvisional: true),
            Month(2021, 3),
        };

        var actual = WeatherStationHistoricDataSummariser.Summarise(historicData);

        actual.Coverage.ProvisionalMonths.Should().Be(2);
    }

    [Fact]
    public void ReturnAnEmptySummaryWhenNothingMatchedInsteadOfThrowing()
    {
        var actual = WeatherStationHistoricDataSummariser.Summarise([]);

        actual.FromYear.Should().BeNull();
        actual.ToYear.Should().BeNull();
        actual.MeanDailyMaxTemperature.Should().BeNull();
        actual.DaysOfAirFrost.Should().BeNull();
        actual.Coverage.MonthsExpected.Should().Be(0);
        actual.Coverage.MonthsReturned.Should().Be(0);
    }

    private static MonthlyReading Month(
        int year,
        int month,
        double? maxTemperature = null,
        double? minTemperature = null,
        int? daysOfAirFrost = null,
        double? rainfall = null,
        double? sunshine = null,
        bool isProvisional = false
    ) =>
        new()
        {
            Year = year,
            Month = month,
            MeanDailyMaxTemperature = maxTemperature,
            MeanDailyMinTemperature = minTemperature,
            DaysOfAirFrost = daysOfAirFrost,
            TotalRainfallMillimeters = rainfall,
            TotalSunshineHours = sunshine,
            IsProvisional = isProvisional,
        };
}
