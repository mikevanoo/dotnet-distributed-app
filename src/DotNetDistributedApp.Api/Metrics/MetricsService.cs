using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DotNetDistributedApp.Api.Metrics;

public class MetricsService : IMetricsService
{
    private readonly Counter<int> _cacheHits;
    private readonly Counter<int> _cacheMisses;
    private readonly Histogram<long> _databaseQueryTime;

    public MetricsService(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("DotNetDistributedApp.Api");
        _cacheHits = meter.CreateCounter<int>("cache.hits");
        _cacheMisses = meter.CreateCounter<int>("cache.misses");
        _databaseQueryTime = meter.CreateHistogram<long>("database.query_time");
    }

    public void CacheHit(int delta, string cacheKey) =>
        _cacheHits.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void CacheMiss(int delta, string cacheKey) =>
        _cacheMisses.Add(delta, new TagList { { "cache_key", cacheKey } });

    public void DatabaseQueryTime(long timeMilliseconds, string queryName) =>
        _databaseQueryTime.Record(timeMilliseconds, new TagList { { "query_name", queryName } });
}
