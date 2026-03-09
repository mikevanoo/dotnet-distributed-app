using AwesomeAssertions;
using DotNetDistributedApp.SpatialApi.CoordinateConverter;

namespace DotNetDistributedApp.IntegrationTests.SpatialApi;

[Trait("Category", "Integration")]
public class CoordinateConverterShould(AppHostFixture appHostFixture)
{
    [Fact]
    public async Task ToOsNationalGridReferenceReturn200OkWithData()
    {
        var response = await CreateClient()
            .GetAsync(
                "/coordinate-converter/to-os-national-grid-reference?latitude=58.214&longitude=-6.318",
                AppHostFixture.CreateCancellationToken()
            );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<OsNationalGridReferenceDto>(model =>
            {
                model.Easting.Should().BePositive();
                model.Northing.Should().BePositive();
            });
    }

    [Fact]
    public async Task ToLatitudeLongitudeReturn200OkWithData()
    {
        var response = await CreateClient()
            .GetAsync(
                "/coordinate-converter/to-latitude-longitude?easting=146400&northing=933200",
                AppHostFixture.CreateCancellationToken()
            );

        response
            .Should()
            .Be200Ok()
            .And.Satisfy<LatitudeLongitudeDto>(model =>
            {
                model.Latitude.Should().BePositive();
                model.Longitude.Should().BeNegative();
            });
    }

    [Theory]
    [InlineData("/coordinate-converter/to-os-national-grid-reference?latitude=91&longitude=0")]
    [InlineData("/coordinate-converter/to-os-national-grid-reference?latitude=-91&longitude=0")]
    [InlineData("/coordinate-converter/to-os-national-grid-reference?latitude=0&longitude=181")]
    [InlineData("/coordinate-converter/to-os-national-grid-reference?latitude=0&longitude=-181")]
    public async Task ToOsNationalGridReferenceReturn400ForOutOfRangeCoordinates(string url)
    {
        var response = await CreateClient().GetAsync(url, AppHostFixture.CreateCancellationToken());

        response.Should().Be400BadRequest();
    }

    [Theory]
    [InlineData("/coordinate-converter/to-latitude-longitude?easting=-1&northing=0")]
    [InlineData("/coordinate-converter/to-latitude-longitude?easting=0&northing=-1")]
    [InlineData("/coordinate-converter/to-latitude-longitude?easting=700001&northing=0")]
    [InlineData("/coordinate-converter/to-latitude-longitude?easting=0&northing=1300001")]
    public async Task ToLatitudeLongitudeReturn400ForOutOfRangeCoordinates(string url)
    {
        var response = await CreateClient().GetAsync(url, AppHostFixture.CreateCancellationToken());

        response.Should().Be400BadRequest();
    }

    private HttpClient CreateClient() => appHostFixture.App.CreateHttpClient("spatial-api");
}
