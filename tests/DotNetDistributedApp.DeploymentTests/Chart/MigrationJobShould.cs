using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>
/// The migration service migrates and exits, so publishing it as a Deployment makes Kubernetes restart
/// it forever. <c>PublishAsKubernetesJob</c> in
/// <c>src/DotNetDistributedApp.AppHost/KubernetesBuilderExtensions.cs</c> exists solely to prevent that,
/// and these are the parts of its output that matter.
/// </summary>
public class MigrationJobShould(ChartFixture chartFixture)
{
    [ChartFact]
    public void BePublishedAsAJobRatherThanADeployment()
    {
        chartFixture.Job(ResourceNames.ApiDatabaseMigrations).Should().NotBeNull();
        chartFixture
            .Deployment(ResourceNames.ApiDatabaseMigrations)
            .Should()
            .BeNull("the Job replaces the Deployment rather than joining it");
    }

    [ChartFact]
    public void RestartOnlyOnFailure() =>
        chartFixture
            .Job(ResourceNames.ApiDatabaseMigrations)!
            .Spec.Template.Spec.RestartPolicy.Should()
            .Be("OnFailure");

    [ChartFact]
    public void RunAsAHelmPostInstallAndPreUpgradeHook()
    {
        var annotations = chartFixture.Job(ResourceNames.ApiDatabaseMigrations)!.Metadata.Annotations;

        // pre-upgrade, not post-upgrade: Helm runs pre-upgrade hooks before applying any updated
        // manifest, so a failed migration aborts the upgrade with the previous ReplicaSet still serving.
        // post-upgrade rolled the new image out first and only then found out the schema was not there.
        // The first install stays post-install because a pre-install hook runs before the ConfigMap,
        // Secret and database it needs exist - and there is no previous version to protect.
        annotations.Should().Contain("helm.sh/hook", "post-install,pre-upgrade");
        annotations.Should().Contain("helm.sh/hook-delete-policy", "before-hook-creation");
    }

    [ChartFact]
    public void RetryAFailedMigration() =>
        chartFixture.Job(ResourceNames.ApiDatabaseMigrations)!.Spec.BackoffLimit.Should().BePositive();

    [ChartFact]
    public void ReuseTheConfigurationOfThePodTemplateThePublisherBuilt()
    {
        var container = chartFixture.Job(ResourceNames.ApiDatabaseMigrations)!.Spec.Template.Spec.Containers.Single();

        container
            .EnvFrom.Should()
            .Contain(source => source.ConfigMapRef != null)
            .And.Contain(source => source.SecretRef != null);
    }
}
