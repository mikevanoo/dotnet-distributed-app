using DotNetDistributedApp.Api.Common.Metrics;
using Microsoft.Extensions.Caching.Hybrid;

namespace DotNetDistributedApp.Api.Clients;

public partial class GeoIpClient(
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

        try
        {
            var result = await cache.GetOrCreateAsync(
                cacheKey,
                async cancellationToken =>
                {
                    cacheMiss = true;
                    LogCacheMiss(cacheKey, ipAddress);
                    metricsService.CacheMiss(1, cacheKey);

                    var response = await httpClient.GetAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        LogCouldNotGetGeoInformation(ipAddress);
                        throw new HttpRequestException(
                            $"GeoIP API returned {response.StatusCode} for IP address {ipAddress}",
                            inner: null,
                            response.StatusCode
                        );
                    }

                    return await response.Content.ReadFromJsonAsync<GeoIpResponseDto>(cancellationToken);
                }
            );

            if (!cacheMiss)
            {
                metricsService.CacheHit(1, cacheKey);
            }

            return result;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [LoggerMessage(
        LogLevel.Information,
        "Cache miss for {CacheKey}, getting geo information for IP address {IpAddress}"
    )]
    private partial void LogCacheMiss(string cacheKey, string ipAddress);

    [LoggerMessage(LogLevel.Warning, "Could not get geo information for IP address {IpAddress}")]
    private partial void LogCouldNotGetGeoInformation(string ipAddress);
}
