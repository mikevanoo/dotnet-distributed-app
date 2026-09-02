using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;
using DotNetDistributedApp.SpatialApi.CoordinateConverter;

namespace DotNetDistributedApp.DeploymentTests.SpatialApi;

/// <summary>
/// <c>spatial-api</c> has no ingress, so it is reached over a port forward. Worth testing directly as
/// well as through <c>api</c>: when the conversion is wrong rather than missing, the weather payload
/// still looks populated.
/// </summary>
public class DeployedCoordinateConverterShould(ClusterFixture clusterFixture) : IAsyncLifetime
{
    private const string ToGridReferenceUrl = "/v1/coordinate-converter/to-os-national-grid-reference";
    private const string ToLatitudeLongitudeUrl = "/v1/coordinate-converter/to-latitude-longitude";

    private PortForward? _portForward;

    public async ValueTask InitializeAsync()
    {
        if (!DeploymentTestEnvironment.ClusterTestsEnabled)
        {
            return;
        }

        _portForward = await clusterFixture.ForwardAsync(
            ResourceNames.SpatialApi,
            8080,
            TestContext.Current.CancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_portForward is not null)
        {
            await _portForward.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [DeploymentFact]
    public async Task Return200OkWithData()
    {
        using var httpClient = CreateClient();

        var response = await httpClient.GetAsync(
            $"{ToGridReferenceUrl}?latitude=51.479&longitude=-0.449",
            TestContext.Current.CancellationToken
        );

        // Despite the endpoint name, the result is a pair of numbers rather than a grid reference string.
        response
            .Should()
            .Be200Ok()
            .And.Satisfy<OsNationalGridReferenceDto>(model =>
            {
                model.Easting.Should().BeApproximately(507805, 5);
                model.Northing.Should().BeApproximately(176700, 5);
            });
    }

    [DeploymentFact]
    public async Task ConvertBackToLatitudeAndLongitude()
    {
        using var httpClient = CreateClient();

        var response = await httpClient.GetAsync(
            $"{ToLatitudeLongitudeUrl}?easting=507800&northing=176100",
            TestContext.Current.CancellationToken
        );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<LatitudeLongitudeDto>(model =>
            {
                model.Latitude.Should().BeApproximately(51.47, 0.1);
                model.Longitude.Should().BeApproximately(-0.45, 0.1);
            });
    }

    [DeploymentTheory]
    [InlineData(ToGridReferenceUrl + "?latitude=91&longitude=0")]
    [InlineData(ToGridReferenceUrl + "?latitude=0&longitude=181")]
    [InlineData(ToLatitudeLongitudeUrl + "?easting=-1&northing=0")]
    [InlineData(ToLatitudeLongitudeUrl + "?easting=0&northing=1300001")]
    public async Task Return400ForOutOfRangeCoordinates(string url)
    {
        using var httpClient = CreateClient();

        var response = await httpClient.GetAsync(url, TestContext.Current.CancellationToken);

        response.Should().Be400BadRequest();
    }

    [DeploymentFact]
    public async Task ReportItselfHealthy()
    {
        using var httpClient = CreateClient();

        var response = await httpClient.GetAsync("/health", TestContext.Current.CancellationToken);

        response.Should().Be200Ok();
    }

    private HttpClient CreateClient() => _portForward!.CreateHttpClient(clusterFixture.Options.RequestTimeout);
}
