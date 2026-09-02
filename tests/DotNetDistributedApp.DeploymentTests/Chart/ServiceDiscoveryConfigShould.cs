using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>
/// The <c>services__*</c> keys are what <c>WithReference</c> becomes once published, and their shape is
/// the reason every client in this app has to use a <c>https+http://</c> base address.
/// </summary>
public class ServiceDiscoveryConfigShould(ChartFixture chartFixture)
{
    private const string ServiceDiscoveryPrefix = "services__";

    [ChartFact]
    public void GiveTheApiAnEndpointForEveryServiceItCalls() =>
        ServiceDiscoveryKeys(ResourceNames.Api)
            .Should()
            .Contain(
                [
                    $"{ServiceDiscoveryPrefix}{ResourceNames.SpatialApi}__http__0",
                    $"{ServiceDiscoveryPrefix}{ResourceNames.GeoIpApi}__http__0",
                ]
            );

    [ChartFact]
    public void GiveTheMcpServerAnEndpointForTheApi() =>
        ServiceDiscoveryKeys(ResourceNames.McpServer)
            .Should()
            .Contain($"{ServiceDiscoveryPrefix}{ResourceNames.Api}__http__0");

    /// <summary>
    /// The chart registers http endpoints only. A client with a single-scheme <c>https://</c> base
    /// address therefore resolves nothing and fails with <c>Name or service not known (spatial-api:443)</c>;
    /// <c>https+http://</c> falls back to the http entry. If an https key ever appears here, that rule
    /// has changed and <c>AllowedSchemes</c> in ServiceDefaults needs revisiting with it.
    /// </summary>
    [ChartFact]
    public void RegisterHttpEndpointsOnly()
    {
        var keys = OnlyServiceDiscoveryKeys(chartFixture.AllConfigKeys()).ToArray();

        keys.Should().NotBeEmpty();
        keys.Should().OnlyContain(key => key.Contains("__http__", StringComparison.Ordinal));
    }

    private IEnumerable<string> ServiceDiscoveryKeys(string component) =>
        OnlyServiceDiscoveryKeys(chartFixture.ConfigKeys(component));

    private static IEnumerable<string> OnlyServiceDiscoveryKeys(IEnumerable<string> keys) =>
        keys.Where(key => key.StartsWith(ServiceDiscoveryPrefix, StringComparison.Ordinal));
}
