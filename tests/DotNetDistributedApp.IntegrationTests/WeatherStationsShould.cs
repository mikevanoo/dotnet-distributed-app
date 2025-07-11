using AwesomeAssertions;

namespace DotNetDistributedApp.IntegrationTests;

public class WeatherStationsShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task GetWeatherStationsReturn200OkAndExpectedNumberOfStations()
    {
        var httpClient = appHostFixture.App.CreateHttpClient("api");
        
        var response = await httpClient.GetAsync("/weather/stations", AppHostFixture.CreateCancellationToken());

        response.Should()
            .Be200Ok()
            .And.Satisfy<IEnumerable<object>>(model => model.Should().HaveCount(2));
    }
    
    [Fact]
    public async Task GetHistoricDataReturns404NotFoundForUnknownStation()
    {
        var httpClient = appHostFixture.App.CreateHttpClient("api");
        
        var response = await httpClient.GetAsync("/weather/stations/unknown-station/historic-data", AppHostFixture.CreateCancellationToken());

        response.Should().Be404NotFound();
    }
    
    [Fact]
    public async Task GetHistoricDataReturns200OkAndDataForKnownStation()
    {
        var httpClient = appHostFixture.App.CreateHttpClient("api");
        
        var response = await httpClient.GetAsync("/weather/stations/heathrow/historic-data", AppHostFixture.CreateCancellationToken());

        response.Should()
            .Be200Ok()
            .And.Satisfy<IEnumerable<object>>(model => model.Should().HaveCountGreaterThan(0));
    }
}
