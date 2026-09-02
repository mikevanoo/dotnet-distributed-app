using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Cluster;

/// <summary>
/// The deployment came up. Everything downstream assumes this, so a failure here is the one worth
/// reading first.
/// </summary>
public class DeployedWorkloadsShould(ClusterFixture clusterFixture)
{
    [DeploymentFact]
    public async Task HaveEveryDeploymentFullyAvailable()
    {
        var deployments = await clusterFixture.ListDeploymentsAsync(TestContext.Current.CancellationToken);

        deployments.Should().NotBeEmpty();
        deployments.Should().OnlyContain(deployment => deployment.Status.ReadyReplicas == deployment.Spec.Replicas);
    }

    [DeploymentFact]
    public async Task HaveEveryStatefulSetFullyAvailable()
    {
        var statefulSets = await clusterFixture.ListStatefulSetsAsync(TestContext.Current.CancellationToken);

        statefulSets.Should().NotBeEmpty();
        statefulSets.Should().OnlyContain(statefulSet => statefulSet.Status.ReadyReplicas == statefulSet.Spec.Replicas);
    }

    [DeploymentFact]
    public async Task HaveCompletedTheMigrationJob()
    {
        var jobs = await clusterFixture.ListJobsAsync(TestContext.Current.CancellationToken);

        jobs.Should()
            .ContainSingle(job => job.Metadata.Name == $"{ResourceNames.ApiDatabaseMigrations}-job")
            .Which.Status.Succeeded.Should()
            .Be(1);
    }

    /// <summary>
    /// A restarting migration pod is the visible symptom of the Job having been published as a
    /// Deployment: the process migrates, exits 0, and Kubernetes starts it again forever.
    /// </summary>
    [DeploymentFact]
    public async Task HaveRunTheMigrationPodExactlyOnce()
    {
        var pods = await clusterFixture.ListPodsAsync(
            ResourceNames.ApiDatabaseMigrations,
            TestContext.Current.CancellationToken
        );

        pods.Should().ContainSingle();
        pods.Single()
            .Status.ContainerStatuses.Should()
            .OnlyContain(containerStatus => containerStatus.RestartCount == 0);
    }

    [DeploymentFact]
    public async Task ExposeAServiceForEveryResourceOtherServicesCall()
    {
        var services = await clusterFixture.ListServicesAsync(TestContext.Current.CancellationToken);

        services
            .Select(service => service.Metadata.Name)
            .Should()
            .Contain(
                [
                    $"{ResourceNames.Api}-service",
                    $"{ResourceNames.SpatialApi}-service",
                    $"{ResourceNames.McpServer}-service",
                    $"{ResourceNames.GeoIpApi}-service",
                    $"{ResourceNames.ApiDatabaseServer}-service",
                    $"{ResourceNames.Cache}-service",
                    $"{ResourceNames.Events}-service",
                ]
            );
    }

    [DeploymentFact]
    public async Task ExposeTheApiThroughAnIngress()
    {
        var ingresses = await clusterFixture.ListIngressesAsync(TestContext.Current.CancellationToken);

        ingresses
            .Should()
            .ContainSingle()
            .Which.Spec.DefaultBackend.Service.Name.Should()
            .Be($"{ResourceNames.Api}-service");
    }

    [DeploymentFact]
    public async Task BindTheDatabaseVolume()
    {
        var claims = await clusterFixture.ListPersistentVolumeClaimsAsync(TestContext.Current.CancellationToken);

        claims
            .Should()
            .ContainSingle(claim => claim.Metadata.Name == ResourceNames.ApiDatabaseData)
            .Which.Status.Phase.Should()
            .Be("Bound");
    }
}
