namespace DotNetDistributedApp.Events.Consumer;

public class RetryDeadLetterOptions
{
    /// <summary>Number of retries after the initial attempt (e.g. 3 means 4 total attempts).</summary>
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Initial delay before the first retry. Used when <see cref="UseExponentialBackoff"/> is false.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>When true, delay doubles each retry (exponential backoff).</summary>
    public bool UseExponentialBackoff { get; set; } = true;

    /// <summary>Maximum delay between retries when using exponential backoff.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Returns the delay to use before the given attempt (1-based).</summary>
    public TimeSpan GetDelay(int attempt)
    {
        if (UseExponentialBackoff)
        {
            var delay = TimeSpan.FromTicks(RetryDelay.Ticks * (1L << (attempt - 1)));
            return delay > MaxRetryDelay ? MaxRetryDelay : delay;
        }
        return RetryDelay;
    }
}
