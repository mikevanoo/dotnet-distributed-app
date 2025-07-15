namespace DotNetDistributedApp.SpatialApi.CoordinateConverter;

public record struct LatitudeLongitudeDto(double Latitude, double Longitude);

public record struct OsNationalGridReferenceDto(double Easting, double Northing);
