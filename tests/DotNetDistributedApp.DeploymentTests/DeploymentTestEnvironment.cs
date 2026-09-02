namespace DotNetDistributedApp.DeploymentTests;

/// <summary>
/// The opt-in switches for this assembly. Both tiers are off by default so that a plain
/// <c>dotnet test</c> - locally or in CI - discovers these tests and skips them instead of failing
/// against a cluster that is not there.
/// </summary>
/// <remarks>
/// Environment variables rather than a live probe: discovery must not depend on the network, and a
/// developer pointed at a stale deployment gets a fixture failure that names the problem rather than
/// a silent skip.
/// </remarks>
public static class DeploymentTestEnvironment
{
    public const string ClusterTestsVariable = "RUN_DEPLOYMENT_TESTS";
    public const string ChartTestsVariable = "RUN_CHART_TESTS";

    public const string ClusterTestsSkipReason =
        $"Requires a deployed cluster. Run ./deployment-test.ps1, or set {ClusterTestsVariable}=1.";

    public const string ChartTestsSkipReason =
        $"Requires the aspire CLI and helm. Run ./deployment-test.ps1 -ChartOnly, or set {ChartTestsVariable}=1.";

    /// <summary>Tiers 2 and 3: everything that talks to a running cluster.</summary>
    public static bool ClusterTestsEnabled { get; } = IsSet(ClusterTestsVariable);

    /// <summary>Tier 1: chart rendering. Needs the aspire CLI and helm, but no cluster.</summary>
    public static bool ChartTestsEnabled { get; } = IsSet(ChartTestsVariable);

    private static bool IsSet(string variable) =>
        Environment.GetEnvironmentVariable(variable) is "1" or "true" or "TRUE";
}
