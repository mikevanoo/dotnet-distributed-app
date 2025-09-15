using Microsoft.Extensions.Caching.Hybrid;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpClient(HttpClient httpClient, HybridCache cache, ILogger<GeoIpClient> logger)
{
    public async Task<GeoIpResponseDto?> GetGeoInformation(string ipAddress)
    {
        var url = $"/{ipAddress}";
        var cacheKey = $"{nameof(GeoIpClient)}:{ipAddress}";

        return await cache.GetOrCreateAsync(
            cacheKey,
            async cancellationToken =>
            {
                logger.LogInformation(
                    "Cache miss for {CacheKey}, getting geo information for IP address {IpAddress}",
                    cacheKey,
                    ipAddress
                );

                var response = await httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Could not get geo information for IP address {IpAddress}", ipAddress);
                    return null;
                }

                var result = await response.Content.ReadFromJsonAsync<GeoIpResponseDto>(cancellationToken);

                return result;
            }
        );
    }
}
