using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Weather;

namespace DotNetDistributedApp.Api.Tests.Weather;

public class GetWeatherStationHistoricDataRequestShould
{
    [Fact]
    public void BeValidWhenNeitherYearIsSupplied()
    {
        var request = new GetWeatherStationHistoricDataRequest { StationKey = "heathrow" };

        Validate(request).Should().BeEmpty();
    }

    [Theory]
    [InlineData(1990, null)]
    [InlineData(null, 1995)]
    [InlineData(1990, 1995)]
    [InlineData(1990, 1990)]
    public void BeValidForYearsInsideTheSupportedRange(int? fromYear, int? toYear)
    {
        var request = new GetWeatherStationHistoricDataRequest
        {
            StationKey = "heathrow",
            FromYear = fromYear,
            ToYear = toYear,
        };

        Validate(request).Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1799)]
    [InlineData(2101)]
    public void RejectFromYearOutsideTheSupportedRange(int fromYear)
    {
        var request = new GetWeatherStationHistoricDataRequest { StationKey = "heathrow", FromYear = fromYear };

        Validate(request)
            .Should()
            .ContainSingle()
            .Which.MemberNames.Should()
            .Contain(nameof(GetWeatherStationHistoricDataRequest.FromYear));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1799)]
    [InlineData(2101)]
    public void RejectToYearOutsideTheSupportedRange(int toYear)
    {
        var request = new GetWeatherStationHistoricDataRequest { StationKey = "heathrow", ToYear = toYear };

        Validate(request)
            .Should()
            .ContainSingle()
            .Which.MemberNames.Should()
            .Contain(nameof(GetWeatherStationHistoricDataRequest.ToYear));
    }

    [Fact]
    public void RejectFromYearLaterThanToYear()
    {
        var request = new GetWeatherStationHistoricDataRequest
        {
            StationKey = "heathrow",
            FromYear = 1995,
            ToYear = 1990,
        };

        var results = Validate(request);

        results.Should().ContainSingle();
        results[0]
            .MemberNames.Should()
            .BeEquivalentTo(
                nameof(GetWeatherStationHistoricDataRequest.FromYear),
                nameof(GetWeatherStationHistoricDataRequest.ToYear)
            );
    }

    private static List<ValidationResult> Validate(GetWeatherStationHistoricDataRequest request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return results;
    }
}
