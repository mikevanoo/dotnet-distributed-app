using System.ComponentModel.DataAnnotations;

namespace DotNetDistributedApp.SpatialApi.CoordinateConverter;

public class ToOsNationalGridReferenceRequest
{
    [Range(-90, 90)]
    public double Latitude { get; init; }

    [Range(-180, 180)]
    public double Longitude { get; init; }
}

public class ToLatitudeLongitudeRequest
{
    [Range(0, 700_000)]
    public double Easting { get; init; }

    [Range(0, 1_300_000)]
    public double Northing { get; init; }
}
