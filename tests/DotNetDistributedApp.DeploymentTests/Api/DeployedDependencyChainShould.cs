using System.Diagnostics;
using System.Net.Http.Json;
using AwesomeAssertions;
using DotNetDistributedApp.Api.Weather;
using DotNetDistributedApp.DeploymentTests.Infrastructure;

namespace DotNetDistributedApp.DeploymentTests.Api;

/// <summary>
/// <c>/weather/stations</c> exercises Postgres, the cache, <c>spatial-api</c> and <c>geoip-api</c> in
/// one call - and degrades rather than fails when any of them is unreachable, because the resilience
/// fallbacks answer 204 and the nullable fields simply vanish from the payload.
/// </summary>
/// <remarks>
/// So the status code proves nothing here and each dependency needs its own assertion on the body.
/// Split into three named tests rather than one: which dependency is down is the useful output, and a
/// single test asserting all three would only ever report the first.
/// </remarks>
public class DeployedDependencyChainShould(ClusterFixture clusterFixture)
{
    [DeploymentFact]
    public async Task ConvertCoordinatesThroughSpatialApi()
    {
        var stations = await GetStationsAsync();

        // Easting and northing come only from spatial-api, and they are nullable - an unreachable
        // spatial-api leaves them absent from an otherwise healthy 200.
        stations.Should().NotBeEmpty().And.OnlyContain(station => station.Easting > 0 && station.Northing > 0);
    }

    [DeploymentFact]
    public async Task ResolveTheCallerLocationThroughGeoIpApi()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync("/v1/weather/stations", TestContext.Current.CancellationToken);

        // The country depends on the cluster's egress address, so only its presence is asserted.
        response
            .Should()
            .Be200Ok()
            .And.Satisfy<ResponseDto<IEnumerable<WeatherStationDto>>>(model =>
            {
                model.Metadata.GeoData.Should().NotBeNull();
                model.Metadata.GeoData!.Country.Should().NotBeEmpty();
            });
    }

    /// <summary>
    /// An unreachable dependency burns its retry and circuit-breaker budget before the fallback answers,
    /// which turns a sub-second call into a roughly eighteen-second one while still returning 200. The
    /// threshold is deliberately generous: the signal is seconds rather than milliseconds.
    /// </summary>
    [DeploymentFact]
    public async Task AnswerWithoutBurningARetryBudget()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        // Warm first, so JIT and a cold cache are not measured as a failing dependency.
        await httpClient.GetAsync("/v1/weather/stations", TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.StartNew();
        await httpClient.GetAsync("/v1/weather/stations", TestContext.Current.CancellationToken);
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(clusterFixture.Options.SlowResponseThreshold);
    }

    private async Task<IEnumerable<WeatherStationDto>> GetStationsAsync()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var envelope = await httpClient.GetFromJsonAsync<ResponseDto<IEnumerable<WeatherStationDto>>>(
            "/v1/weather/stations",
            TestContext.Current.CancellationToken
        );

        return envelope!.Response;
    }
}
