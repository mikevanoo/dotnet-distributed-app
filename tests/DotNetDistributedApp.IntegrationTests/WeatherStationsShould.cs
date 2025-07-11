using AwesomeAssertions;

namespace DotNetDistributedApp.IntegrationTests;

public class WeatherStationsShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task ReturnExpectedNumberOfStations()
    {
        var httpClient = appHostFixture.App.CreateHttpClient("api");
        
        var response = await httpClient.GetAsync("/weather/stations", AppHostFixture.CreateCancellationToken());

        response.Should()
            .Be200Ok()
            .And.Satisfy<IEnumerable<object>>(model => model.Should().HaveCount(2));
    }
}
