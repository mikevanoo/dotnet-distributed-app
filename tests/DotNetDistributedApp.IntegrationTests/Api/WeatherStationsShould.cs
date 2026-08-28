using AwesomeAssertions;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.IntegrationTests.Api;

public class WeatherStationsShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task GetWeatherStationsReturn200OkAndExpectedNumberOfStations()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync("/v1.0/weather/stations", TestContext.Current.CancellationToken);

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<object>>>(model => model.Response.Should().HaveCount(2));
    }

    [Fact]
    public async Task GetWeatherStationsReturn200OkAndExpectedGeoInformation()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync("/v1.0/weather/stations", TestContext.Current.CancellationToken);

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<object>>>(model =>
            {
                model.Metadata.GeoData.Should().NotBeNull();
                model.Metadata.GeoData.Country.Should().NotBeEmpty();
            });
    }

    [Fact]
    public async Task GetHistoricDataReturns404NotFoundForUnknownStation()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            "/weather/stations/unknown-station/historic-data",
            TestContext.Current.CancellationToken
        );

        response.Should().Be404NotFound();
    }

    [Fact]
    public async Task GetHistoricDataReturns200OkAndDataForKnownStation()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            "/v1.0/weather/stations/heathrow/historic-data",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<object>>>(model => model.Response.Should().HaveCountGreaterThan(0));
    }

    [Fact]
    public async Task GetHistoricDataReturnsOnlyReadingsWithinTheRequestedYearRange()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            "/v1.0/weather/stations/heathrow/historic-data?fromYear=1990&toYear=1995",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationHistoricDataDto>>>(model =>
                model.Response.Should().OnlyContain(data => data.Year >= 1990 && data.Year <= 1995).And.NotBeEmpty()
            );
    }

    [Fact]
    public async Task GetHistoricDataAppliesFromYearWithoutToYear()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            "/v1.0/weather/stations/heathrow/historic-data?fromYear=2015",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationHistoricDataDto>>>(model =>
                model.Response.Should().OnlyContain(data => data.Year >= 2015).And.NotBeEmpty()
            );
    }

    [Theory]
    [InlineData("?fromYear=0")]
    [InlineData("?fromYear=-1")]
    [InlineData("?toYear=2101")]
    public async Task GetHistoricDataReturns400BadRequestForYearsOutsideTheSupportedRange(string queryString)
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            $"/v1.0/weather/stations/heathrow/historic-data{queryString}",
            TestContext.Current.CancellationToken
        );

        response.Should().Be400BadRequest();
    }

    [Fact]
    public async Task GetHistoricDataReturns400BadRequestWhenFromYearIsLaterThanToYear()
    {
        var httpClient = appHostFixture.App.CreateHttpClient(ResourceNames.Api);

        var response = await httpClient.GetAsync(
            "/v1.0/weather/stations/heathrow/historic-data?fromYear=1995&toYear=1990",
            TestContext.Current.CancellationToken
        );

        response.Should().Be400BadRequest();
    }
}
