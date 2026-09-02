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
    private const string ConnectionFailure = "Microsoft.EntityFrameworkCore.Database.Connection[20004]";

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

        log.Should().NotContain("Unhandled exception");
        UnexpectedFailures(log).Should().BeEmpty();
    }

    /// <summary>
    /// Every <c>fail:</c> line other than the one a first deploy always logs.
    /// </summary>
    /// <remarks>
    /// <c>MigrateAsync</c> finds out whether the database exists by opening a connection to it, so
    /// against an empty volume that probe fails with <c>3D000 invalid_catalog_name</c> and EF Core
    /// logs it at Error level - immediately before the <c>CREATE DATABASE</c> that resolves it. So a
    /// completely healthy first deploy logs one connection failure, and a redeploy onto the existing
    /// volume logs none. Excluding the event id unconditionally would also hide a database that is
    /// genuinely unreachable, so it only counts as expected when this run went on to create the
    /// database.
    /// </remarks>
    private static string[] UnexpectedFailures(string log)
    {
        var createdTheDatabase = log.Contains("CREATE DATABASE", StringComparison.Ordinal);

        return log.Split('\n')
            .Where(line => line.StartsWith("fail:", StringComparison.Ordinal))
            .Where(line => !createdTheDatabase || !line.Contains(ConnectionFailure, StringComparison.Ordinal))
            .ToArray();
    }

    private Task<string> ReadLogAsync() =>
        clusterFixture.GetPodLogAsync(
            ResourceNames.ApiDatabaseMigrations,
            TestContext.Current.CancellationToken,
            tailLines: null
        );
}
