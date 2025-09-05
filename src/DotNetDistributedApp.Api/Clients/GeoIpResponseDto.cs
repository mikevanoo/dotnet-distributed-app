namespace DotNetDistributedApp.Api.Clients;

public class GeoIpResponseDto
{
    public string Country { get; set; }
    public string Latitude { get; set; }
    public string Longitude { get; set; }
    public string Continent { get; set; }
    public string Timezone { get; set; }
    public int AccuracyRadius { get; set; }
    public int Asn { get; set; }
    public string AsnOrganization { get; set; }
    public string AsnNetwork { get; set; }
}
