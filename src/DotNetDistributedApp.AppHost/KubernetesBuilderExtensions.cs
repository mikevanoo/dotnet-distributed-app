using Aspire.Hosting.Kubernetes.Resources;

namespace DotNetDistributedApp.AppHost;

public static class KubernetesBuilderExtensions
{
    /// <summary>
    /// Pins a resource to a single replica in the generated Helm chart. Several services in this app
    /// cannot be scaled horizontally; each call site documents why.
    /// </summary>
    public static IResourceBuilder<T> PinToSingleReplica<T>(this IResourceBuilder<T> builder)
        where T : IComputeResource =>
        builder.PublishAsKubernetesService(resource =>
        {
            if (resource.Workload is Deployment deployment)
            {
                deployment.Spec.Replicas = 1;
            }
        });

    /// <summary>
    /// Publishes a run-once resource as a <c>batch/v1</c> Job wired up as a Helm
    /// <c>post-install</c>/<c>pre-upgrade</c> hook, replacing the Deployment the publisher would
    /// otherwise emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process that exits when its work is done cannot be a Deployment: Kubernetes restarts it
    /// forever. Aspire's Kubernetes publisher models only Deployment and StatefulSet, so the Job has
    /// to be added by hand - see <see cref="JobV1" />.
    /// </para>
    /// <para>
    /// <c>pre-upgrade</c> makes the Job a gate on every redeploy. Helm runs pre-upgrade hooks before
    /// it applies a single updated manifest, so a migration that exhausts its <paramref name="backoffLimit" />
    /// fails the upgrade with nothing applied and the previous ReplicaSet still serving. It is also the
    /// right ordering for a schema change: the migration runs while the old code is live, which is what
    /// a backwards-compatible migration is written for. Under <c>post-upgrade</c> - the original choice -
    /// the new image was already rolled out and serving against an unmigrated schema by the time the Job
    /// started.
    /// </para>
    /// <para>
    /// The first install cannot be gated the same way, hence the pair. Helm runs pre-install hooks
    /// before any release resource exists, so a <c>pre-install</c> Job would find neither the ConfigMap
    /// and Secret it reads its connection string from nor the database it connects to. There is nothing
    /// to protect on a first install anyway - no previous version is serving - so it stays
    /// <c>post-install</c>, which Helm still waits on before reporting the release installed. Dependent
    /// services therefore start before the schema does on that one deploy and log connection or
    /// missing-relation errors for a few seconds; the migration's EF Core execution strategy and the
    /// services' <c>EnableRetryOnFailure</c> absorb it.
    /// </para>
    /// <para>
    /// Two consequences of gating this way. The Job's <c>envFrom</c> resolves the *previous* revision's
    /// ConfigMap and Secret, because Helm updates release resources only after the hook succeeds - the
    /// image comes from the new values, but a deploy that changes the database host or password in the
    /// same revision migrates using the old one. And Helm's <c>--timeout</c> (5 minutes by default)
    /// expires before a <paramref name="backoffLimit" /> of 10 does, so a genuinely broken migration
    /// fails the upgrade while its Job keeps retrying in the background.
    /// </para>
    /// <para>
    /// This still does not reproduce <c>WaitForCompletion</c>, which is a run-mode-only gate. It gates
    /// the release, not the pods; gating pod startup needs an init container on each dependent.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<T> PublishAsKubernetesJob<T>(this IResourceBuilder<T> builder, int backoffLimit = 10)
        where T : IComputeResource =>
        builder.PublishAsKubernetesService(resource =>
        {
            if (resource.Workload is not Deployment deployment)
            {
                return;
            }

            // Reuse the pod template the publisher already built so the Job runs the same image with
            // the same ConfigMap and Secret references.
            var template = deployment.Spec.Template;
            template.Spec.RestartPolicy = "OnFailure";

            var job = new JobV1 { Spec = { BackoffLimit = backoffLimit, Template = template } };
            job.Metadata.Name = $"{builder.Resource.Name}-job";
            job.Metadata.Annotations["helm.sh/hook"] = "post-install,pre-upgrade";
            job.Metadata.Annotations["helm.sh/hook-weight"] = "-5";
            job.Metadata.Annotations["helm.sh/hook-delete-policy"] = "before-hook-creation";

            resource.AdditionalResources.Add(job);

            // The Job fully replaces the Deployment.
            resource.Workload = null;
        });
}
