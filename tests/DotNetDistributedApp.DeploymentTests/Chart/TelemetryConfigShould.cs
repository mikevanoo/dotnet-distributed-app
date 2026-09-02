using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>
/// The deployed dashboard has no resource service, so OTLP is the only thing it knows about the
/// release. A service missing these two keys simply never appears on the telemetry pages, with no
/// error anywhere to explain the gap.
/// </summary>
public class TelemetryConfigShould(ChartFixture chartFixture)
{
    private static readonly string[] TelemetrySendingComponents =
    [
        ResourceNames.Api,
        ResourceNames.SpatialApi,
        ResourceNames.McpServer,
        ResourceNames.EventsConsumer,
        ResourceNames.ScheduledTasks,
        ResourceNames.ApiDatabaseMigrations,
    ];

    [ChartFact]
    public void PointEveryServiceAtTheDashboardCollector()
    {
        var endpoints = TelemetrySendingComponents.Select(component =>
            chartFixture.ConfigValue(component, "OTEL_EXPORTER_OTLP_ENDPOINT")
        );

        endpoints
            .Should()
            .AllBe($"http://{ResourceNames.KubernetesEnvironment}-dashboard-service:18889")
            .And.HaveCount(TelemetrySendingComponents.Length);
    }

    [ChartFact]
    public void NameEachServiceDistinctlyInTelemetry()
    {
        var serviceNames = TelemetrySendingComponents
            .Select(component => chartFixture.ConfigValue(component, "OTEL_SERVICE_NAME"))
            .ToArray();

        serviceNames.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(TelemetrySendingComponents);
    }
}
