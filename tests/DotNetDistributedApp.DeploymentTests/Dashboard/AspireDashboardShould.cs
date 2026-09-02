using System.Text.RegularExpressions;
using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.ServiceDefaults;

namespace DotNetDistributedApp.DeploymentTests.Dashboard;

/// <summary>
/// The deployed dashboard, which is both the release's UI and its OTLP collector - every service points
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> at it. Verified without a browser: the token URL sets the auth
/// cookie, so an <see cref="HttpClient" /> with a cookie container is enough.
/// </summary>
/// <remarks>
/// This is the standalone dashboard image with no resource service, so the Resources page is
/// permanently empty and only the telemetry pages carry data. Those three are what is asserted.
/// </remarks>
public partial class AspireDashboardShould(ClusterFixture clusterFixture) : IAsyncLifetime
{
    private const string DashboardComponent = $"{ResourceNames.KubernetesEnvironment}-dashboard";
    private const int UiPort = 18888;
    private const int OtlpHttpPort = 18890;

    private PortForward? _portForward;

    public async ValueTask InitializeAsync()
    {
        if (!DeploymentTestEnvironment.ClusterTestsEnabled)
        {
            return;
        }

        _portForward = await clusterFixture.ForwardAsync(
            DashboardComponent,
            UiPort,
            TestContext.Current.CancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_portForward is not null)
        {
            await _portForward.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    [DeploymentFact]
    public async Task RedirectAnAnonymousVisitorToTheLoginPage()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = _portForward!.BaseAddress,
            Timeout = clusterFixture.Options.RequestTimeout,
        };

        var response = await httpClient.GetAsync("/traces", TestContext.Current.CancellationToken);

        response.Should().Be302Found();
        response.Headers.Location!.PathAndQuery.Should().Be("/login?returnUrl=%2Ftraces");
    }

    [DeploymentTheory]
    [InlineData("/structuredlogs")]
    [InlineData("/traces")]
    [InlineData("/metrics")]
    public async Task ServeTheTelemetryPagesToAnAuthenticatedVisitor(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = _portForward!.BaseAddress,
            Timeout = clusterFixture.Options.RequestTimeout,
        };

        // Visiting the token URL is what sets the auth cookie on this handler's container.
        var loginResponse = await httpClient.GetAsync(await GetLoginPathAsync(cancellationToken), cancellationToken);
        loginResponse.Should().Be200Ok();
        var response = await httpClient.GetAsync(path, cancellationToken);

        response.Should().Be200Ok();
    }

    /// <summary>
    /// The OTLP/HTTP receiver, checked before blaming any exporter. An empty protobuf body is a valid
    /// empty export, so a 200 means the receiver is up - and, since the chart sets no
    /// <c>DASHBOARD__OTLP__AUTHMODE</c>, that it is unsecured. Fine on a local cluster; not on AKS.
    /// </summary>
    [DeploymentFact]
    public async Task AcceptAnUnauthenticatedOtlpExport()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var otlpForward = await clusterFixture.ForwardAsync(
            DashboardComponent,
            OtlpHttpPort,
            cancellationToken
        );
        using var httpClient = otlpForward.CreateHttpClient(clusterFixture.Options.RequestTimeout);
        using var body = new ByteArrayContent([]);
        body.Headers.ContentType = new("application/x-protobuf");

        var response = await httpClient.PostAsync("/v1/traces", body, cancellationToken);

        response.Should().Be200Ok();
    }

    /// <summary>
    /// The login token is generated at startup and changes on every pod restart, so it has to be read
    /// out of the log rather than configured. The URL the pod prints names port 18888 because that is
    /// the port the pod sees; only the path and its token query are usable here, since the forward is on
    /// an ephemeral local port.
    /// </summary>
    private async Task<string> GetLoginPathAsync(CancellationToken cancellationToken)
    {
        var log = await clusterFixture.GetPodLogAsync(DashboardComponent, cancellationToken, tailLines: null);
        var match = LoginUrlPattern().Match(log);

        match.Success.Should().BeTrue("the dashboard log should print a login URL at startup");

        return new Uri(match.Groups["url"].Value).PathAndQuery;
    }

    [GeneratedRegex(@"Login URL:\s+(?<url>\S+)")]
    private static partial Regex LoginUrlPattern();
}
