using System.Text;
using System.Text.RegularExpressions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using k8s;
using k8s.Models;
using Xunit.Sdk;
using Xunit.v3;

[assembly: AssemblyFixture(typeof(ClusterFixture))]

// Every test in this assembly writes to one shared deployment - it publishes Kafka events, fills the
// output cache and adds inbox rows. Running classes in parallel would make the inbox count deltas in
// TransactionalInboxShould race each other and would open a dozen simultaneous port forwards for no
// gain, since the wall-clock cost here is waiting on a cluster rather than local CPU.
[assembly: Parallelization(Mode = ParallelMode.None)]

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>
/// One connection to the deployed release, shared by every test class: the Kubernetes API client, an
/// <see cref="HttpClient" /> for the ingress, and the waiting-for-ready that would otherwise be paid
/// per class.
/// </summary>
/// <remarks>
/// Nothing here runs unless <see cref="DeploymentTestEnvironment.ClusterTestsEnabled" /> is set. The
/// fixture is still constructed when the tests are skipped, so construction stays free and every
/// resource is built in <see cref="InitializeAsync" />.
/// </remarks>
public partial class ClusterFixture : IAsyncLifetime
{
    private const string ComponentLabel = "app.kubernetes.io/component";

    private Kubernetes? _client;

    public DeploymentTestOptions Options { get; } = DeploymentTestOptions.Load();

    public string Namespace => Options.Namespace;

    public IKubernetes Client =>
        _client ?? throw new InvalidOperationException("The cluster fixture has not been initialised.");

    public async ValueTask InitializeAsync()
    {
        if (!DeploymentTestEnvironment.ClusterTestsEnabled)
        {
            return;
        }

        var configuration = KubernetesClientConfiguration.BuildConfigFromConfigFile();

        // These tests are not read-only, and `aspire deploy` has no --context flag, so a stale current
        // context is the realistic way to point them at the wrong cluster. Fail before touching it.
        if (
            !string.IsNullOrWhiteSpace(Options.ExpectedKubeContext)
            && !string.Equals(configuration.CurrentContext, Options.ExpectedKubeContext, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Refusing to run against kubeconfig context '{configuration.CurrentContext}'. These tests write "
                    + $"to their target, so they only run against '{Options.ExpectedKubeContext}'. Switch context or "
                    + "override DeploymentTests__ExpectedKubeContext."
            );
        }

        _client = new Kubernetes(configuration);

        await WaitForNamespaceAsync();
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    /// <summary>An <see cref="HttpClient" /> for the ingress, which is the only routed service.</summary>
    public HttpClient CreateApiClient() =>
        new() { BaseAddress = new Uri(Options.BaseUrl), Timeout = Options.RequestTimeout };

    public async Task<IList<V1Pod>> ListPodsAsync(CancellationToken cancellationToken) =>
        (await Client.CoreV1.ListNamespacedPodAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1Deployment>> ListDeploymentsAsync(CancellationToken cancellationToken) =>
        (await Client.AppsV1.ListNamespacedDeploymentAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1StatefulSet>> ListStatefulSetsAsync(CancellationToken cancellationToken) =>
        (await Client.AppsV1.ListNamespacedStatefulSetAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1Job>> ListJobsAsync(CancellationToken cancellationToken) =>
        (await Client.BatchV1.ListNamespacedJobAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1Service>> ListServicesAsync(CancellationToken cancellationToken) =>
        (await Client.CoreV1.ListNamespacedServiceAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1Ingress>> ListIngressesAsync(CancellationToken cancellationToken) =>
        (await Client.NetworkingV1.ListNamespacedIngressAsync(Namespace, cancellationToken: cancellationToken)).Items;

    public async Task<IList<V1PersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(
        CancellationToken cancellationToken
    ) =>
        (
            await Client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(
                Namespace,
                cancellationToken: cancellationToken
            )
        ).Items;

    /// <summary>The pods of one Aspire resource, found by the component label the chart applies.</summary>
    public async Task<IList<V1Pod>> ListPodsAsync(string component, CancellationToken cancellationToken) =>
        (
            await Client.CoreV1.ListNamespacedPodAsync(
                Namespace,
                labelSelector: $"{ComponentLabel}={component}",
                cancellationToken: cancellationToken
            )
        ).Items;

    /// <summary>A running pod of one Aspire resource, for port forwarding or reading a log.</summary>
    public async Task<V1Pod> GetRunningPodAsync(string component, CancellationToken cancellationToken) =>
        await Eventually.ForAsync(
            $"a running '{component}' pod",
            async token =>
                (await ListPodsAsync(component, token)).FirstOrDefault(pod => pod.Status?.Phase == "Running"),
            pod => pod is not null,
            Options.WorkloadReadyTimeout,
            cancellationToken
        ) ?? throw new InvalidOperationException($"No running pod for component '{component}'.");

    /// <summary>
    /// A pod's log, with terminal colour codes stripped. <paramref name="sinceSeconds" /> is what makes
    /// matching on log text reliable: it bounds the window to the test's own activity instead of
    /// whatever the pod has said all day.
    /// </summary>
    /// <remarks>
    /// The stripping is not cosmetic. These services configure Serilog's console sink with
    /// <c>AnsiConsoleTheme.Code</c> and <c>applyThemeToRedirectedOutput: true</c>, so the log a pod
    /// writes has SGR escape sequences <i>inside</i> each message - every templated parameter is wrapped
    /// in them. "dead letter topic common-dlq" is really "dead letter topic <esc>common-dlq<esc>", and
    /// "attempt 1/4" is really "attempt <esc>1<esc>/<esc>4<esc>", so a substring search for either finds
    /// nothing while the log plainly appears to contain it.
    /// </remarks>
    public async Task<string> GetPodLogAsync(
        string component,
        CancellationToken cancellationToken,
        int? tailLines = 200,
        int? sinceSeconds = null,
        bool includePreviousPod = false
    )
    {
        var pods = await ListPodsAsync(component, cancellationToken);
        var pod =
            pods.FirstOrDefault(candidate => candidate.Status?.Phase == "Running")
            ?? pods.FirstOrDefault()
            ?? throw new InvalidOperationException($"No pod for component '{component}'.");

        await using var stream = await Client.CoreV1.ReadNamespacedPodLogAsync(
            pod.Metadata.Name,
            Namespace,
            tailLines: tailLines,
            sinceSeconds: sinceSeconds,
            previous: includePreviousPod,
            cancellationToken: cancellationToken
        );
        using var reader = new StreamReader(stream);

        return AnsiEscapeCodePattern().Replace(await reader.ReadToEndAsync(cancellationToken), string.Empty);
    }

    public async Task<PortForward> ForwardAsync(string component, int remotePort, CancellationToken cancellationToken)
    {
        var pod = await GetRunningPodAsync(component, cancellationToken);

        return PortForward.Start(Client, Namespace, pod.Metadata.Name, remotePort);
    }

    /// <summary>Reads one key out of a release secret, decoding the base64 the API server returns.</summary>
    public async Task<string> GetSecretValueAsync(string secretName, string key, CancellationToken cancellationToken)
    {
        var secret = await Client.CoreV1.ReadNamespacedSecretAsync(
            secretName,
            Namespace,
            cancellationToken: cancellationToken
        );

        return secret.Data.TryGetValue(key, out var value)
            ? Encoding.UTF8.GetString(value)
            : throw new InvalidOperationException($"Secret '{secretName}' has no key '{key}'.");
    }

    /// <summary>
    /// The environment a deployed pod actually sees, which is what proves service discovery was wired
    /// rather than merely rendered. Read from the container spec's ConfigMap and Secret references, so
    /// it needs no exec into the pod.
    /// </summary>
    public async Task<IDictionary<string, string>> GetContainerEnvironmentAsync(
        string component,
        CancellationToken cancellationToken
    )
    {
        var pod = await GetRunningPodAsync(component, cancellationToken);
        var container = pod.Spec.Containers.Single();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var source in container.EnvFrom ?? Array.Empty<V1EnvFromSource>())
        {
            if (source.ConfigMapRef is not null)
            {
                var configMap = await Client.CoreV1.ReadNamespacedConfigMapAsync(
                    source.ConfigMapRef.Name,
                    Namespace,
                    cancellationToken: cancellationToken
                );

                foreach (var entry in configMap.Data ?? new Dictionary<string, string>())
                {
                    environment[entry.Key] = entry.Value;
                }
            }

            if (source.SecretRef is not null)
            {
                var secret = await Client.CoreV1.ReadNamespacedSecretAsync(
                    source.SecretRef.Name,
                    Namespace,
                    cancellationToken: cancellationToken
                );

                foreach (var entry in secret.Data ?? new Dictionary<string, byte[]>())
                {
                    environment[entry.Key] = Encoding.UTF8.GetString(entry.Value);
                }
            }
        }

        foreach (var entry in container.Env ?? Array.Empty<V1EnvVar>())
        {
            if (entry.Value is not null)
            {
                environment[entry.Name] = entry.Value;
            }
        }

        return environment;
    }

    /// <summary>Select Graphic Rendition sequences: ESC [ then digits and semicolons then 'm'.</summary>
    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiEscapeCodePattern();

    private async Task WaitForNamespaceAsync()
    {
        using var timeout = new CancellationTokenSource(Options.WorkloadReadyTimeout);

        try
        {
            await Client.CoreV1.ReadNamespaceAsync(Namespace, cancellationToken: timeout.Token);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Namespace '{Namespace}' was not found in context '{Options.ExpectedKubeContext}'. Deploy the app "
                    + "first - see docs/K8S-DEPLOYMENT-TESTING-COMMANDS.md.",
                exception
            );
        }
    }
}
