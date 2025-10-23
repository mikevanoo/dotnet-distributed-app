namespace DotNetDistributedApp.Api.Metrics;

public interface IMetricsService
{
    public void CacheHit(int delta, string cacheKey);
    public void CacheMiss(int delta, string cacheKey);
    public void DatabaseQueryTime(long timeMilliseconds, string queryName);
}
