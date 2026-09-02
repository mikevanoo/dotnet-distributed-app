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
    /// <c>post-install</c>/<c>post-upgrade</c> hook, replacing the Deployment the publisher would
    /// otherwise emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A process that exits when its work is done cannot be a Deployment: Kubernetes restarts it
    /// forever. Aspire's Kubernetes publisher models only Deployment and StatefulSet, so the Job has
    /// to be added by hand - see <see cref="JobV1" />.
    /// </para>
    /// <para>
    /// The hook is <c>post-install</c> and not <c>pre-install</c> on purpose. Helm runs pre-install
    /// hooks before any release resource exists, so the Job would find neither the ConfigMap and
    /// Secret it reads its connection string from nor the database it connects to. Running after
    /// install means both exist. Helm still waits for the hook to succeed before reporting the
    /// release installed.
    /// </para>
    /// <para>
    /// The trade-off is that dependent services start before the schema is in place and will log
    /// connection or missing-relation errors for the first few seconds. That is survivable here
    /// because the migration itself retries through an EF Core execution strategy and the services
    /// use <c>EnableRetryOnFailure</c>. It does not reproduce <c>WaitForCompletion</c>, which is a
    /// run-mode-only gate; gating deployed pods needs an init container on each dependent.
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
            job.Metadata.Annotations["helm.sh/hook"] = "post-install,post-upgrade";
            job.Metadata.Annotations["helm.sh/hook-weight"] = "-5";
            job.Metadata.Annotations["helm.sh/hook-delete-policy"] = "before-hook-creation";

            resource.AdditionalResources.Add(job);

            // The Job fully replaces the Deployment.
            resource.Workload = null;
        });
}
