using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetDistributedApp.Api.Common.Metrics;

public class MetricsService : IMetricsService
{
    private readonly Counter<int> _cacheHits;
    private readonly Counter<int> _cacheMisses;
    private readonly Histogram<long> _databaseQueryTime;
    private readonly Counter<int> _sendEventSuccess;
    private readonly Counter<int> _sendEventFailed;

    public MetricsService(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("DotNetDistributedApp.Api");
        _cacheHits = meter.CreateCounter<int>("cache.hits");
        _cacheMisses = meter.CreateCounter<int>("cache.misses");
        _databaseQueryTime = meter.CreateHistogram<long>("database.query_time");
        _sendEventSuccess = meter.CreateCounter<int>("events.send_success");
        _sendEventFailed = meter.CreateCounter<int>("events.send_failed");
    }

    public void CacheHit(int delta, string cacheKey) =>
        _cacheHits.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void CacheMiss(int delta, string cacheKey) =>
        _cacheMisses.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void DatabaseQueryTime(long timeMilliseconds, string queryName) =>
        _databaseQueryTime.Record(timeMilliseconds, new TagList { { "query_name", queryName } });

    public void SendEventSuccess(int delta, string topic, string eventName) =>
        _sendEventSuccess.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });

    public void SendEventFailed(int delta, string topic, string eventName) =>
        _sendEventFailed.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });
}
