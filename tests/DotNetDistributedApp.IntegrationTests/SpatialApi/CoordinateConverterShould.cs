// using AwesomeAssertions;
// using DotNetDistributedApp.SpatialApi.CoordinateConverter;
//
// namespace DotNetDistributedApp.IntegrationTests.SpatialApi;
//
// public class CoordinateConverterShould(AppHostFixture appHostFixture)
// {
//     [Fact]
//     public async Task ToOsNationalGridReferenceReturn200OkWithData()
//     {
//         var httpClient = appHostFixture.App.CreateHttpClient("spatial-api");
//
//         var response = await httpClient.GetAsync(
//             "/coordinate-converter/to-os-national-grid-reference?latitude=58.214&longitude=-6.318",
//             AppHostFixture.CreateCancellationToken()
//         );
//
//         response
//             .Should()
//             .Be200Ok()
//             .And.Satisfy<OsNationalGridReferenceDto>(model =>
//             {
//                 model.Easting.Should().BePositive();
//                 model.Northing.Should().BePositive();
//             });
//     }
//
//     [Fact]
//     public async Task ToLatitudeLongitudeReturn200OkWithData()
//     {
//         var httpClient = appHostFixture.App.CreateHttpClient("spatial-api");
//
//         var response = await httpClient.GetAsync(
//             "/coordinate-converter/to-latitude-longitude?easting=146400&northing=933200",
//             AppHostFixture.CreateCancellationToken()
//         );
//
//         response
//             .Should()
//             .Be200Ok()
//             .And.Satisfy<LatitudeLongitudeDto>(model =>
//             {
//                 model.Latitude.Should().BePositive();
//                 model.Longitude.Should().BeNegative();
//             });
//     }
// }
