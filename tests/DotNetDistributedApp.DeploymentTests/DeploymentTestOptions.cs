using Microsoft.Extensions.Configuration;

namespace DotNetDistributedApp.DeploymentTests;

/// <summary>
/// Where the tests point and how long they are willing to wait. Everything comes from
/// <c>appsettings.json</c> and can be overridden per environment with <c>DeploymentTests__*</c>
/// environment variables, which is the whole of what changes between the local cluster and AKS: the
/// URLs move, the Kubernetes API calls do not.
/// </summary>
public class DeploymentTestOptions
{
    public string BaseUrl { get; set; } = "http://localhost";
    public string Namespace { get; set; } = "dotnet-distributed-app";
    public string ReleaseName { get; set; } = "dotnet-distributed-app";

    /// <summary>
    /// The kubeconfig context these tests are allowed to touch. They publish events and write inbox
    /// rows, so pointing them at the wrong cluster is not a read-only mistake - <see
    /// cref="Infrastructure.ClusterFixture" /> refuses to start when the current context is anything
    /// else. Blank disables the check.
    /// </summary>
    public string ExpectedKubeContext { get; set; } = "rancher-desktop";

    public string HelmChartName { get; set; } = "dotnet-distributed-app";
    public string AppHostProjectPath { get; set; } = "src/DotNetDistributedApp.AppHost";
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int WorkloadReadyTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Above this, a 200 means a dependency is unreachable and burning its retry budget before the
    /// resilience fallback answers. Generous on purpose: the point is to catch seconds-not-
    /// milliseconds, not to benchmark a laptop.
    /// </summary>
    public int SlowResponseThresholdSeconds { get; set; } = 5;

    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(RequestTimeoutSeconds);
    public TimeSpan WorkloadReadyTimeout => TimeSpan.FromSeconds(WorkloadReadyTimeoutSeconds);
    public TimeSpan SlowResponseThreshold => TimeSpan.FromSeconds(SlowResponseThresholdSeconds);

    public static DeploymentTestOptions Load()
    {
        var options = new DeploymentTestOptions();

        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build()
            .GetSection("DeploymentTests")
            .Bind(options);

        return options;
    }
}
