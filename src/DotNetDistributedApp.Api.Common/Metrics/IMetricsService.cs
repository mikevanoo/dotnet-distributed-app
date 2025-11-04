namespace DotNetDistributedApp.Api.Common.Metrics;

public interface IMetricsService
{
    public void CacheHit(int delta, string cacheKey);
    public void CacheMiss(int delta, string cacheKey);
    public void DatabaseQueryTime(long timeMilliseconds, string queryName);
    public void ProduceEventSuccess(int delta, string topic, string eventName);
    public void ProduceEventFailed(int delta, string topic, string eventName);
    public void ConsumeEventSuccess(int delta, string topic, string eventName);
    public void ConsumeEventFailed(int delta, string topic, string eventName);
    public void ConsumeEventUnrecognised(int delta, string topic, string eventName);
}
