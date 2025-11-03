using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Caching.Hybrid;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpClient(
    HttpClient httpClient,
    HybridCache cache,
    ILogger<GeoIpClient> logger,
    IMetricsService metricsService
)
{
    public async Task<GeoIpResponseDto?> GetGeoInformation(string ipAddress)
    {
        var url = $"/{ipAddress}";
        var cacheKey = $"{nameof(GeoIpClient)}:{ipAddress}";
        var cacheMiss = false;

        var result = await cache.GetOrCreateAsync(
            cacheKey,
            async cancellationToken =>
            {
                cacheMiss = true;
                logger.LogInformation(
                    "Cache miss for {CacheKey}, getting geo information for IP address {IpAddress}",
                    cacheKey,
                    ipAddress
                );
                metricsService.CacheMiss(1, cacheKey);

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

        if (!cacheMiss)
        {
            metricsService.CacheHit(1, cacheKey);
        }

        return result;
    }
}
