using Aspire.Hosting.Kubernetes.Resources;
using YamlDotNet.Serialization;

namespace DotNetDistributedApp.AppHost;

// Aspire's Kubernetes publisher models only Deployment and StatefulSet workloads, so a run-once
// resource has no representation. These two types add the missing batch/v1 Job so a migration can be
// emitted as a Helm hook instead of a Deployment that exits and is restarted forever.
public sealed class JobV1() : BaseKubernetesResource("batch/v1", "Job")
{
    [YamlMember(Alias = "spec")]
    public JobSpecV1 Spec { get; set; } = new();
}

public sealed class JobSpecV1
{
    [YamlMember(Alias = "backoffLimit")]
    public int? BackoffLimit { get; set; }

    [YamlMember(Alias = "template")]
    public PodTemplateSpecV1 Template { get; set; } = new();
}
