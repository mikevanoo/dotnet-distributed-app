using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>
/// Three services cannot be scaled horizontally, each for its own reason, and each failure is silent
/// rather than loud - a second replica does not crash, it just does nothing useful or does the same
/// work twice.
/// </summary>
/// <remarks>
/// This guards against a <c>WithReplicas(n)</c> or an HPA being added, not against
/// <c>PinToSingleReplica</c> being removed: the publisher's own default is 1, so dropping the pin
/// changes nothing today. What it pins down is the deployed reality, whatever produces it.
/// </remarks>
public class SingleReplicaResourcesShould(ChartFixture chartFixture)
{
    [ChartTheory]
    // events-consumer: topic `common` has one partition, so a second replica in the same consumer
    // group is assigned nothing and idles. Concurrency comes from WithWorkersCount instead.
    [InlineData(ResourceNames.EventsConsumer)]
    // scheduled-tasks: Coravel's PreventOverlapping mutex is in-process, so two replicas both fire
    // every minute and both delete from the same inbox.
    [InlineData(ResourceNames.ScheduledTasks)]
    // mcp-server: SessionMode is StatefulForInitializeClients, so MCP sessions live in pod memory and
    // a client whose next request lands on the other replica is told its session does not exist.
    [InlineData(ResourceNames.McpServer)]
    public void RunExactlyOneReplica(string component)
    {
        var deployment = chartFixture.Deployment(component);

        deployment.Should().NotBeNull();
        deployment!.Spec.Replicas.Should().Be(1);
    }
}
