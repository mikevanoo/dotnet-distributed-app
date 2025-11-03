namespace DotNetDistributedApp.Api.Common.Metrics;

public interface IMetricsService
{
    public void CacheHit(int delta, string cacheKey);
    public void CacheMiss(int delta, string cacheKey);
    public void DatabaseQueryTime(long timeMilliseconds, string queryName);
    public void SendEventSuccess(int delta, string topic, string eventName);
    public void SendEventFailed(int delta, string topic, string eventName);
}
