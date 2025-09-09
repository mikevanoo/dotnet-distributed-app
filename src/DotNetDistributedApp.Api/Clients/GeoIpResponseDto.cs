namespace DotNetDistributedApp.Api.Clients;

public class GeoIpResponseDto
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
