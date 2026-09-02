using System.Net.Http.Json;
using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;

namespace DotNetDistributedApp.DeploymentTests.Api;

/// <summary>
/// The three event endpoints. They only publish to Kafka, so all they can prove is that the producer
/// reached the broker - what the consumer then did with the message is
/// <see cref="Events.TransactionalInboxShould" /> and <see cref="Events.EventRetryLadderShould" />.
/// </summary>
public class EventEndpointsShould(ClusterFixture clusterFixture)
{
    [DeploymentFact]
    public async Task AcceptASimpleEvent()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.PostAsJsonAsync(
            "/v1/events/simple-event",
            new { value = $"deployment-test-{Guid.CreateVersion7()}" },
            TestContext.Current.CancellationToken
        );

        response.Should().Be200Ok();
    }

    [DeploymentFact]
    public async Task AcceptADuplicatePairOfEvents()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.PostAsJsonAsync(
            "/v1/events/duplicate-event",
            new { value = $"deployment-test-{Guid.CreateVersion7()}" },
            TestContext.Current.CancellationToken
        );

        response.Should().Be200Ok();
    }

    /// <summary>
    /// The failing event takes no body and always throws in its handler. The endpoint still succeeds:
    /// publishing worked, and the failure happens later in the consumer.
    /// </summary>
    [DeploymentFact]
    public async Task AcceptAFailingEvent()
    {
        using var httpClient = clusterFixture.CreateApiClient();

        var response = await httpClient.PostAsync(
            "/v1/events/failing-event",
            null,
            TestContext.Current.CancellationToken
        );

        response.Should().Be200Ok();
    }
}
