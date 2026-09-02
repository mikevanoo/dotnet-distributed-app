using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;
using k8s.Models;

namespace DotNetDistributedApp.DeploymentTests.Chart;

/// <summary>
/// <c>api</c> is the only resource that should be reachable from outside the cluster. Everything else
/// is <c>ClusterIP</c> and stays that way.
/// </summary>
public class IngressShould(ChartFixture chartFixture)
{
    [ChartFact]
    public void UseTheTraefikIngressClass() => SingleIngress().Spec.IngressClassName.Should().Be("traefik");

    [ChartFact]
    public void SendEverythingToTheApiServiceByDefault()
    {
        var backend = SingleIngress().Spec.DefaultBackend;

        // A default backend and no host rule is what makes any hostname work, which is what lets the
        // tests reach the release at plain http://localhost through Traefik.
        backend.Service.Name.Should().Be($"{ResourceNames.Api}-service");
        backend.Service.Port.Name.Should().Be("http");
    }

    [ChartFact]
    public void RouteNoServiceOtherThanTheApi() => SingleIngress().Spec.Rules.Should().BeNullOrEmpty();

    private V1Ingress SingleIngress() => chartFixture.Manifests<V1Ingress>().Single();
}
