using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetDistributedApp.Api.Common.Metrics;

public class MetricsService : IMetricsService
{
    private readonly Counter<int> _cacheHits;
    private readonly Counter<int> _cacheMisses;
    private readonly Histogram<long> _databaseQueryTime;
    private readonly Counter<int> _produceEventSuccess;
    private readonly Counter<int> _produceEventFailed;
    private readonly Counter<int> _consumeEventSuccess;
    private readonly Counter<int> _consumeEventFailed;
    private readonly Counter<int> _consumeEventUnrecognised;

    public MetricsService(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("DotNetDistributedApp.Api");
        _cacheHits = meter.CreateCounter<int>("cache.hits");
        _cacheMisses = meter.CreateCounter<int>("cache.misses");
        _databaseQueryTime = meter.CreateHistogram<long>("database.query_time");
        _produceEventSuccess = meter.CreateCounter<int>("events.produce_success");
        _produceEventFailed = meter.CreateCounter<int>("events.produce_failed");
        _consumeEventSuccess = meter.CreateCounter<int>("events.consume_success");
        _consumeEventFailed = meter.CreateCounter<int>("events.consume_failed");
        _consumeEventUnrecognised = meter.CreateCounter<int>("events.consume_unrecognised");
    }

    public void CacheHit(int delta, string cacheKey) =>
        _cacheHits.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void CacheMiss(int delta, string cacheKey) =>
        _cacheMisses.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void DatabaseQueryTime(long timeMilliseconds, string queryName) =>
        _databaseQueryTime.Record(timeMilliseconds, new TagList { { "query_name", queryName } });

    public void ProduceEventSuccess(int delta, string topic, string eventName) =>
        _produceEventSuccess.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });

    public void ProduceEventFailed(int delta, string topic, string eventName) =>
        _produceEventFailed.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });

    public void ConsumeEventSuccess(int delta, string topic, string eventName) =>
        _consumeEventSuccess.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });

    public void ConsumeEventFailed(int delta, string topic, string eventName) =>
        _consumeEventFailed.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });

    public void ConsumeEventUnrecognised(int delta, string topic, string eventName) =>
        _consumeEventUnrecognised.Add(delta, new TagList { { "topic", topic }, { "event_name", eventName } });
}
