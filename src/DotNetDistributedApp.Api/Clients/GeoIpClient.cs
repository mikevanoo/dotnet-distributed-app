using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Caching.Hybrid;

namespace DotNetDistributedApp.Api.Clients;

public class GeoIpClient(
    HttpClient httpClient,
    HybridCache cache,
    GeoIpClientLogger logger,
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
                logger.CacheMiss(cacheKey, ipAddress);
                metricsService.CacheMiss(1, cacheKey);

                var response = await httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.CouldNotGetGeoInformation(ipAddress);
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

public partial class GeoIpClientLogger(ILogger<GeoIpClient> logger)
{
    [LoggerMessage(
        LogLevel.Information,
        "Cache miss for {cacheKey}, getting geo information for IP address {ipAddress}"
    )]
    public partial void CacheMiss(string cacheKey, string ipAddress);

    [LoggerMessage(LogLevel.Warning, "Could not get geo information for IP address {ipAddress}")]
    public partial void CouldNotGetGeoInformation(string ipAddress);
}
