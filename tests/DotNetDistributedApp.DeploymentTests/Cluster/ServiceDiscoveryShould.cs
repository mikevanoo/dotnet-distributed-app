using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Cluster;

/// <summary>
/// The same <c>services__*</c> keys the chart tier asserts, read back off the deployed pods. The chart
/// tier proves they were rendered; this proves they reached the container, which is a different failure
/// - a ConfigMap that was never applied, or a pod running an older revision of one.
/// </summary>
public class ServiceDiscoveryShould(ClusterFixture clusterFixture)
{
    private const string ServiceDiscoveryPrefix = "services__";

    [DeploymentFact]
    public async Task ResolveSpatialApiAndGeoIpForTheApiPod()
    {
        var environment = await clusterFixture.GetContainerEnvironmentAsync(
            ResourceNames.Api,
            TestContext.Current.CancellationToken
        );

        environment
            .Should()
            .Contain(
                $"{ServiceDiscoveryPrefix}{ResourceNames.SpatialApi}__http__0",
                $"http://{ResourceNames.SpatialApi}-service:8080"
            );
        environment.Keys.Should().Contain($"{ServiceDiscoveryPrefix}{ResourceNames.GeoIpApi}__http__0");
    }

    [DeploymentFact]
    public async Task ResolveTheApiForTheMcpServerPod()
    {
        var environment = await clusterFixture.GetContainerEnvironmentAsync(
            ResourceNames.McpServer,
            TestContext.Current.CancellationToken
        );

        environment
            .Should()
            .Contain(
                $"{ServiceDiscoveryPrefix}{ResourceNames.Api}__http__0",
                $"http://{ResourceNames.Api}-service:8080"
            );
    }

    /// <summary>
    /// Only http endpoints are registered, in the cluster as in the chart. This is the fact that makes
    /// <c>https+http://</c> mandatory in every client base address in the app.
    /// </summary>
    [DeploymentTheory]
    [InlineData(ResourceNames.Api)]
    [InlineData(ResourceNames.McpServer)]
    public async Task RegisterHttpEndpointsOnly(string component)
    {
        var environment = await clusterFixture.GetContainerEnvironmentAsync(
            component,
            TestContext.Current.CancellationToken
        );
        var serviceDiscoveryKeys = environment
            .Keys.Where(key => key.StartsWith(ServiceDiscoveryPrefix, StringComparison.Ordinal))
            .ToArray();

        serviceDiscoveryKeys.Should().NotBeEmpty();
        serviceDiscoveryKeys.Should().OnlyContain(key => key.Contains("__http__", StringComparison.Ordinal));
    }

    [DeploymentFact]
    public async Task PointTheApiPodAtTheDashboardCollector()
    {
        var environment = await clusterFixture.GetContainerEnvironmentAsync(
            ResourceNames.Api,
            TestContext.Current.CancellationToken
        );

        environment
            .Should()
            .Contain(
                "OTEL_EXPORTER_OTLP_ENDPOINT",
                $"http://{ResourceNames.KubernetesEnvironment}-dashboard-service:18889"
            );
    }
}
