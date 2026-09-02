using System.Diagnostics;

namespace DotNetDistributedApp.DeploymentTests;

/// <summary>
/// Polls until a condition holds. A deployed system answers late rather than wrongly - a pod becomes
/// ready, a Kafka message reaches the consumer, an inbox row appears - so almost every assertion here
/// needs a window rather than an instant.
/// </summary>
public static class Eventually
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Probes until <paramref name="isSatisfied" /> accepts the result, then returns it. On timeout the
    /// failure carries the last value observed, which is what tells you whether nothing happened or the
    /// wrong thing happened.
    /// </summary>
    public static async Task<T> ForAsync<T>(
        string description,
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> isSatisfied,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null
    )
    {
        var deadline = Stopwatch.StartNew();
        var lastResult = default(T);
        var lastError = default(Exception);

        while (deadline.Elapsed < timeout)
        {
            try
            {
                lastResult = await probe(cancellationToken);
                if (isSatisfied(lastResult))
                {
                    return lastResult;
                }

                lastError = null;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A deployed dependency that is still starting throws rather than answering, so a
                // failed probe is retried like an unsatisfied one until the timeout runs out.
                lastError = exception;
            }

            await Task.Delay(pollInterval ?? DefaultPollInterval, cancellationToken);
        }

        var observed = lastError is not null
            ? $"last error: {lastError.Message}"
            : $"last value: {Describe(lastResult)}";

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:0.#}s waiting for {description}. {observed}"
        );
    }

    /// <summary>Polls until <paramref name="condition" /> is true.</summary>
    public static Task UntilAsync(
        string description,
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null
    ) => ForAsync(description, condition, satisfied => satisfied, timeout, cancellationToken, pollInterval);

    private static string Describe<T>(T? value) =>
        value switch
        {
            null => "<null>",
            IEnumerable<string> strings => $"[{string.Join(", ", strings)}]",
            _ => value.ToString() ?? "<null>",
        };
}
