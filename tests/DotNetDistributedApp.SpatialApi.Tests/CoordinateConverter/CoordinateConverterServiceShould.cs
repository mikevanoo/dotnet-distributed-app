using AwesomeAssertions;
using DotNetDistributedApp.SpatialApi.CoordinateConverter;

namespace DotNetDistributedApp.SpatialApi.Tests.CoordinateConverter;

public class CoordinateConverterServiceShould()
{
    [Theory]
    [InlineData("Stornoway", 58.214f, -6.318f, 146400, 933200)]
    [InlineData("Heathrow", 51.479f, -0.449, 507800, 176700)]
    public void ConvertFromLatitudeLongitudeToOsNationalGridReference(
        string location,
        double latitude,
        double longitude,
        double expectedEasting,
        double expectedNorthing
    )
    {
        const double OneHundredMeters = 100f;

        var actual = CoordinateConverterService.ToOsgb36(latitude, longitude);
        actual.IsSuccess.Should().BeTrue();
        var (actualEasting, actualNorthing) = actual.Value;

        actualEasting
            .Should()
            .BeApproximately(expectedEasting, OneHundredMeters, $"{location} easting is {expectedEasting}");
        actualNorthing
            .Should()
            .BeApproximately(expectedNorthing, OneHundredMeters, $"{location} northing is {expectedNorthing}");
    }

    [Theory]
    [InlineData("Stornoway", 146400, 933200, 58.214f, -6.318f)]
    [InlineData("Heathrow", 507800, 176700, 51.479f, -0.449)]
    public void ConvertFromOsNationalGridReferenceToLatitudeLongitude(
        string location,
        double easting,
        double northing,
        double expectedLatitude,
        double expectedLongitude
    )
    {
        const double Precision = 0.01f;

        var actual = CoordinateConverterService.ToWgs84(easting, northing);
        actual.IsSuccess.Should().BeTrue();
        var (actualLatitude, actualLongitude) = actual.Value;

        actualLatitude
            .Should()
            .BeApproximately(expectedLatitude, Precision, $"{location} latitude is {expectedLatitude}");
        actualLongitude
            .Should()
            .BeApproximately(expectedLongitude, Precision, $"{location} longitude is {expectedLongitude}");
    }
}
