using System.Text.Json.Serialization;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpResponseDto
{
    public required string Country { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public required double Latitude { get; set; }

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public required double Longitude { get; set; }

    public required string Continent { get; set; }
    public required string Timezone { get; set; }
    public int AccuracyRadius { get; set; }
    public int Asn { get; set; }
    public required string AsnOrganization { get; set; }
    public required string AsnNetwork { get; set; }
}
