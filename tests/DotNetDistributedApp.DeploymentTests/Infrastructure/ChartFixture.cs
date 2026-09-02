using DotNetDistributedApp.DeploymentTests.Infrastructure;
using k8s;
using k8s.Models;

[assembly: AssemblyFixture(typeof(ChartFixture))]

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>
/// Renders the Helm chart the app model produces, once, and hands the tests typed manifests.
/// </summary>
/// <remarks>
/// <para>
/// <c>aspire publish</c> then <c>helm template</c>, with no cluster involved. That ordering matters:
/// the templates the publisher writes are Go templates referencing <c>.Values</c>, so they cannot be
/// deserialised directly - only what helm renders is valid YAML.
/// </para>
/// <para>
/// These are the invariants that cost the most to discover after a deploy: a migration published as a
/// Deployment, a resource that scaled past the one replica it can tolerate, a dev-only tool that made
/// it into the release. Asserting them here catches all three in seconds.
/// </para>
/// </remarks>
public class ChartFixture : IAsyncLifetime
{
    private readonly List<object> _manifests = [];
    private string? _renderedYaml;

    public DeploymentTestOptions Options { get; } = DeploymentTestOptions.Load();

    /// <summary>The rendered YAML, for the few assertions that are about absence rather than shape.</summary>
    public string RenderedYaml =>
        _renderedYaml ?? throw new InvalidOperationException("The chart fixture has not been initialised.");

    public async ValueTask InitializeAsync()
    {
        if (!DeploymentTestEnvironment.ChartTestsEnabled)
        {
            return;
        }

        var repositoryRoot = CommandLine.FindRepositoryRoot();
        var outputPath = Path.Combine(Path.GetTempPath(), $"aspire-chart-tests-{Guid.NewGuid():N}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        try
        {
            var publish = await CommandLine.RunAsync(
                "aspire",
                ["publish", "--apphost", Options.AppHostProjectPath, "-o", outputPath, "--non-interactive", "--nologo"],
                repositoryRoot,
                timeout.Token
            );

            if (publish.ExitCode != 0)
            {
                throw new InvalidOperationException($"'aspire publish' failed: {publish.Describe()}");
            }

            var lint = await CommandLine.RunAsync("helm", ["lint", outputPath], repositoryRoot, timeout.Token);

            if (lint.ExitCode != 0)
            {
                throw new InvalidOperationException($"'helm lint' failed: {lint.Describe()}");
            }

            var template = await CommandLine.RunAsync(
                "helm",
                ["template", Options.ReleaseName, outputPath],
                repositoryRoot,
                timeout.Token
            );

            if (template.ExitCode != 0)
            {
                throw new InvalidOperationException($"'helm template' failed: {template.Describe()}");
            }

            _renderedYaml = template.StandardOutput;
            _manifests.AddRange(KubernetesYaml.LoadAllFromString(_renderedYaml));
        }
        finally
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        return ValueTask.CompletedTask;
    }

    public IEnumerable<T> Manifests<T>() => _manifests.OfType<T>();

    public V1Deployment? Deployment(string component) => FindByComponent<V1Deployment>(component);

    public V1StatefulSet? StatefulSet(string component) => FindByComponent<V1StatefulSet>(component);

    public V1Job? Job(string component) => FindByComponent<V1Job>(component);

    public V1ConfigMap? ConfigMap(string component) => FindByComponent<V1ConfigMap>(component);

    /// <summary>One rendered environment variable of one component, or null if it is not set.</summary>
    public string? ConfigValue(string component, string key) =>
        ConfigMap(component)?.Data is { } data && data.TryGetValue(key, out var value) ? value : null;

    /// <summary>Every environment variable key the chart sets on one component.</summary>
    public IEnumerable<string> ConfigKeys(string component) => ConfigMap(component)?.Data?.Keys ?? [];

    /// <summary>Every environment variable key the chart sets on any component.</summary>
    public IEnumerable<string> AllConfigKeys() =>
        Manifests<V1ConfigMap>().SelectMany(configMap => configMap.Data?.Keys ?? []);

    public V1Service? Service(string component) => FindByComponent<V1Service>(component);

    /// <summary>
    /// Every component name the chart emits any workload for. Used to assert on absence: a dev-only
    /// tool that lost its <c>ExcludeFromManifest()</c> shows up here before it shows up in a cluster.
    /// </summary>
    public IReadOnlySet<string> WorkloadComponents() =>
        _manifests
            .Select(manifest =>
                manifest switch
                {
                    V1Deployment deployment => ComponentOf(deployment.Metadata),
                    V1StatefulSet statefulSet => ComponentOf(statefulSet.Metadata),
                    V1Job job => ComponentOf(job.Metadata),
                    _ => null,
                }
            )
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private T? FindByComponent<T>(string component)
        where T : class, IMetadata<V1ObjectMeta> =>
        _manifests
            .OfType<T>()
            .FirstOrDefault(manifest =>
                string.Equals(ComponentOf(manifest.Metadata), component, StringComparison.Ordinal)
            );

    private static string? ComponentOf(V1ObjectMeta? metadata) =>
        metadata?.Labels is { } labels && labels.TryGetValue("app.kubernetes.io/component", out var component)
            ? component
            : null;
}
