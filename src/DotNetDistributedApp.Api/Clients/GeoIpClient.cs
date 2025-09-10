using DotNetDistributedApp.Api.Common.Errors;
using FluentResults;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpClient(HttpClient httpClient, ILogger<GeoIpClient> logger)
{
    public async Task<GeoIpResponseDto?> GetGeoInformation(string ipAddress)
    {
        var url = $"/{ipAddress}";
        var response = await httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Could not get geo information for IP address {IpAddress}", ipAddress);
            return null;
        }

        var result = await response.Content.ReadFromJsonAsync<GeoIpResponseDto>();

        return result;
    }
}
