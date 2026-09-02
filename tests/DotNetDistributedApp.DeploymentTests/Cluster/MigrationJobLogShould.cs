using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Cluster;

/// <summary>
/// The migration service has no telemetry of its own and exits before anything could scrape it, so its
/// pod log is the only place a failed migration ever surfaces. A Job that reports success having
/// swallowed an error still leaves the schema wrong, and every later assertion then fails somewhere
/// unrelated.
/// </summary>
public class MigrationJobLogShould(ClusterFixture clusterFixture)
{
    [DeploymentFact]
    public async Task ReportACleanShutdown()
    {
        var log = await ReadLogAsync();

        log.Should().Contain("Application is shutting down");
    }

    [DeploymentFact]
    public async Task ReportNoFailure()
    {
        var log = await ReadLogAsync();

        log.Should().NotContain("Unhandled exception").And.NotContain("fail:");
    }

    private Task<string> ReadLogAsync() =>
        clusterFixture.GetPodLogAsync(
            ResourceNames.ApiDatabaseMigrations,
            TestContext.Current.CancellationToken,
            tailLines: null
        );
}
