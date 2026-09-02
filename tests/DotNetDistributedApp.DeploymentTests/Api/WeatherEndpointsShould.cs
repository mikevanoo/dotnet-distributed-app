using AwesomeAssertions;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.DeploymentTests.Infrastructure;

namespace DotNetDistributedApp.DeploymentTests.Api;

/// <summary>
/// The weather endpoints through the ingress. Shape and status codes only - whether the dependencies
/// behind them actually answered is <see cref="DeployedDependencyChainShould" />, because a broken
/// dependency still returns 200 here.
/// </summary>
public class WeatherEndpointsShould(ClusterFixture clusterFixture)
{
    [DeploymentTheory]
    [InlineData("/v1/weather/stations")]
    [InlineData("/v2/weather/stations")]
    public async Task ReturnTheSeededStations(string path)
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(path, TestContext.Current.CancellationToken);

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationDto>>>(model =>
                model.Response.Select(station => station.Key).Should().BeEquivalentTo(["heathrow", "stornoway"])
            );
    }

    [DeploymentFact]
    public async Task ReturnMoreStationsOnV2Only()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var found = await httpClient.GetAsync("/v2/weather/more-stations", TestContext.Current.CancellationToken);
        var notFound = await httpClient.GetAsync("/v1/weather/more-stations", TestContext.Current.CancellationToken);

        found.Should().Be200Ok();
        notFound.Should().Be404NotFound();
    }

    [DeploymentFact]
    public async Task ReturnHistoricDataForAKnownStation()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(
            "/v1/weather/stations/heathrow/historic-data",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationHistoricDataDto>>>(model =>
                model.Response.Should().NotBeEmpty()
            );
    }

    [DeploymentFact]
    public async Task FilterHistoricDataToTheRequestedYearRange()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(
            "/v1/weather/stations/heathrow/historic-data?fromYear=1950&toYear=1960",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationHistoricDataDto>>>(model =>
                model.Response.Should().NotBeEmpty().And.OnlyContain(data => data.Year >= 1950 && data.Year <= 1960)
            );
    }

    [DeploymentFact]
    public async Task Return404ForAnUnknownStation()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(
            "/v1/weather/stations/unknown-station/historic-data",
            TestContext.Current.CancellationToken
        );

        response.Should().Be404NotFound();
    }

    [DeploymentTheory]
    [InlineData("?fromYear=0")]
    [InlineData("?toYear=2101")]
    [InlineData("?fromYear=1995&toYear=1990")]
    public async Task Return400ForAnInvalidYearRange(string queryString)
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(
            $"/v1/weather/stations/heathrow/historic-data{queryString}",
            TestContext.Current.CancellationToken
        );

        response.Should().Be400BadRequest();
    }

    /// <summary>
    /// Output caching is on for 30 seconds, and the envelope carries a <c>metadata.timestamp</c> stamped
    /// when the response is built - so a byte-identical second body is proof the cache served it, with
    /// none of the flakiness of asserting that the second call was faster.
    /// </summary>
    [DeploymentFact]
    public async Task ServeHistoricDataFromValkeyOnASecondCall()
    {
        using var httpClient = clusterFixture.CreateApiClient();
        const string path = "/v1/weather/stations/stornoway/historic-data?fromYear=1970&toYear=1975";

        var first = await httpClient.GetStringAsync(path, TestContext.Current.CancellationToken);
        var second = await httpClient.GetStringAsync(path, TestContext.Current.CancellationToken);

        second.Should().Be(first);
    }
}
