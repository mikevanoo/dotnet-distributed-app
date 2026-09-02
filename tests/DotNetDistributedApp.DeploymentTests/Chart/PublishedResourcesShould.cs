using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;
using k8s.Models;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>What the chart contains, and - more usefully - what it must not contain.</summary>
public class PublishedResourcesShould(ChartFixture chartFixture)
{
    /// <summary>
    /// The developer-tools containers. <c>WithExplicitStart()</c> alone does not keep a resource out of
    /// a published manifest; only <c>ExcludeFromManifest()</c> does, and losing that call ships a
    /// database admin UI to the cluster without any error to notice.
    /// </summary>
    private static readonly string[] DeveloperOnlyComponents =
    [
        "pgadmin",
        "redisinsight",
        "kafka-ui",
        ResourceNames.McpInspector,
    ];

    [ChartFact]
    public void PublishAWorkloadForEveryDeployedResource() =>
        chartFixture
            .WorkloadComponents()
            .Should()
            .Contain(
                [
                    ResourceNames.Api,
                    ResourceNames.SpatialApi,
                    ResourceNames.McpServer,
                    ResourceNames.EventsConsumer,
                    ResourceNames.ScheduledTasks,
                    ResourceNames.ApiDatabaseMigrations,
                    ResourceNames.ApiDatabaseServer,
                    ResourceNames.Cache,
                    ResourceNames.Events,
                    ResourceNames.GeoIpApi,
                ]
            );

    [ChartFact]
    public void PublishNoDeveloperOnlyTool() =>
        chartFixture.WorkloadComponents().Should().NotContain(DeveloperOnlyComponents);

    [ChartFact]
    public void MountTheDatabaseOnItsOwnPersistentVolumeClaim()
    {
        var claim = chartFixture.Manifests<V1PersistentVolumeClaim>().Single();

        // Without a claim the database lands on an emptyDir and every pod restart wipes it. The
        // storage class is the one thing that changes on AKS, where it becomes managed-csi.
        claim.Metadata.Name.Should().Be(ResourceNames.ApiDatabaseData);
        claim.Spec.StorageClassName.Should().Be("local-path");
        claim.Spec.AccessModes.Should().ContainSingle().Which.Should().Be("ReadWriteOnce");
        claim.Spec.Resources.Requests["storage"].ToString().Should().Be("2Gi");
    }

    [ChartFact]
    public void KeepTheDatabaseServerAsAStatefulSetBoundToThatClaim()
    {
        var statefulSet = chartFixture.StatefulSet(ResourceNames.ApiDatabaseServer);

        statefulSet.Should().NotBeNull();
        statefulSet!
            .Spec.Template.Spec.Volumes.Should()
            .Contain(volume => volume.PersistentVolumeClaim.ClaimName == ResourceNames.ApiDatabaseData);
    }
}
