using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;

namespace DotNetDistributedApp.DeploymentTests.Api;

/// <summary>
/// Both health endpoints answer in every environment. They were once inside an
/// <c>IsDevelopment()</c> block, which meant a deployed pod 404'd its own probes - a regression that
/// costs nothing to guard and is invisible until a readiness probe starts failing.
/// </summary>
public class HealthEndpointsShould(ClusterFixture clusterFixture)
{
    [DeploymentTheory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Return200Ok(string path)
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.GetAsync(path, TestContext.Current.CancellationToken);

        response.Should().Be200Ok();
    }
}
