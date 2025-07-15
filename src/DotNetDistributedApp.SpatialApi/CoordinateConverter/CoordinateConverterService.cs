using DotSpatial.Projections;
using FluentResults;

namespace DotNetDistributedApp.SpatialApi.CoordinateConverter;

public static class CoordinateConverterService
{
    // Define the projection systems using their EPSG codes
    // WGS84 is the standard for GPS (Latitude/Longitude)
    private static readonly ProjectionInfo Wgs84 = ProjectionInfo.FromEpsgCode(4326);

    // British National Grid
    private static readonly ProjectionInfo Osgb36 = ProjectionInfo.FromEpsgCode(27700);

    /// <summary>
    /// Converts Latitude and Longitude (WGS84) to an OS National Grid reference (OSGB36).
    /// </summary>
    /// <param name="latitude">The latitude value.</param>
    /// <param name="longitude">The longitude value.</param>
    /// <returns>A record containing the Easting and Northing.</returns>
    public static Result<OsNationalGridReferenceDto> ToOsgb36(double latitude, double longitude)
    {
        // The input for ReprojectPoints must be an array of doubles: [x, y, x, y, ...]
        double[] xy = [longitude, latitude];

        // Z-values (elevation) are not used in this 2D conversion
        double[] z = [0];

        // Perform the reprojection from WGS84 to OSGB36
        Reproject.ReprojectPoints(xy, z, Wgs84, Osgb36, 0, 1);

        // The xy array is modified in place with the new coordinates
        return Result.Ok(new OsNationalGridReferenceDto(xy[0], xy[1]));
    }

    /// <summary>
    /// Converts an OS National Grid reference (Easting, Northing) to Latitude and Longitude.
    /// </summary>
    /// <param name="easting">The Easting value.</param>
    /// <param name="northing">The Northing value.</param>
    /// <returns>A record containing the Latitude and Longitude.</returns>
    public static Result<LatitudeLongitudeDto> ToWgs84(double easting, double northing)
    {
        double[] xy = [easting, northing];
        double[] z = [0];

        // Perform the reprojection from OSGB36 to WGS84
        Reproject.ReprojectPoints(xy, z, Osgb36, Wgs84, 0, 1);

        // Return the reprojected coordinates
        return Result.Ok(new LatitudeLongitudeDto(xy[1], xy[0]));
    }
}
