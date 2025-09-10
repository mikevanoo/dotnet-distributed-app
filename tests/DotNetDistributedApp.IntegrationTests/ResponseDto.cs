namespace DotNetDistributedApp.IntegrationTests;

public class ResponseDto
{
    public required ResponseMetadataDto Metadata { get; set; }
}

public class ResponseDto<T>() : ResponseDto
{
    public required T Response { get; set; }
}

public class ResponseMetadataDto
{
    public DateTimeOffset Timestamp { get; set; }
    public ResponseGeoDataDto GeoData { get; set; }
}

public class ResponseGeoDataDto
{
    public required string Country { get; set; }
    public required string Latitude { get; set; }
    public required string Longitude { get; set; }
    public required string Continent { get; set; }
    public required string Timezone { get; set; }
    public int AccuracyRadius { get; set; }
    public int Asn { get; set; }
    public required string AsnOrganization { get; set; }
    public required string AsnNetwork { get; set; }
}
