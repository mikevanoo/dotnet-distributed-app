using System.Text.Json;
using AwesomeAssertions;
using DotNetDistributedApp.DeploymentTests.Infrastructure;
using DotNetDistributedApp.McpServer.Tools;
using DotNetDistributedApp.ServiceDefaults;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotNetDistributedApp.DeploymentTests.McpServer;

/// <summary>
/// The MCP server, driven over a port forward by a real MCP client rather than by hand-rolled JSON-RPC.
/// </summary>
/// <remarks>
/// <para>
/// This is the end-to-end check worth having, because the MCP server holds no data of its own: every
/// tool calls <c>api</c> over service discovery. A populated result therefore proves the chain from a
/// second pod, through <c>api</c>, to <c>spatial-api</c> and Postgres.
/// </para>
/// <para>
/// The client also removes three things the protocol makes easy to get wrong by hand - accepting
/// <c>text/event-stream</c>, echoing the <c>Mcp-Session-Id</c> header, and completing the handshake
/// with <c>notifications/initialized</c>. One session is shared by the whole class because
/// <c>SessionMode</c> is <c>StatefulForInitializeClients</c> and the session lives in the pod's memory.
/// </para>
/// </remarks>
public class DeployedMcpServerShould(ClusterFixture clusterFixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions StructuredContentOptions = new(JsonSerializerDefaults.Web);

    private PortForward? _portForward;
    private McpClient? _mcpClient;

    private McpClient Client => _mcpClient!;

    public async ValueTask InitializeAsync()
    {
        if (!DeploymentTestEnvironment.ClusterTestsEnabled)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        _portForward = await clusterFixture.ForwardAsync(ResourceNames.McpServer, 8080, cancellationToken);
        _mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(_portForward.BaseAddress, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                }
            ),
            cancellationToken: cancellationToken
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcpClient is not null)
        {
            await _mcpClient.DisposeAsync();
        }

        if (_portForward is not null)
        {
            await _portForward.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Plain HTTP, so a failure here rules out the port forward before the protocol is blamed.</summary>
    [DeploymentFact]
    public async Task ReportItselfHealthy()
    {
        using var httpClient = _portForward!.CreateHttpClient(clusterFixture.Options.RequestTimeout);

        var response = await httpClient.GetAsync("/health", TestContext.Current.CancellationToken);

        response.Should().Be200Ok();
    }

    [DeploymentFact]
    public void IdentifyItself() => Client.ServerInfo.Name.Should().Be("DotNetDistributedApp.McpServer");

    [DeploymentFact]
    public async Task ExposeExactlyTheThreeWeatherTools()
    {
        var tools = await Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools
            .Select(tool => tool.Name)
            .Should()
            .BeEquivalentTo(["list_weather_stations", "get_station_historic_data", "summarise_station_historic_data"]);
    }

    /// <summary>
    /// Every tool sets <c>UseStructuredContent</c>, so the structured payload is the contract and the
    /// text block need not be parsed.
    /// </summary>
    [DeploymentFact]
    public async Task ListStationsWithCoordinatesResolvedThroughTheApi()
    {
        var result = await CallAsync("list_weather_stations", new Dictionary<string, object?>());

        var stations = Deserialise<WeatherStationsDto>(result).Stations;
        stations.Select(station => station.Key).Should().BeEquivalentTo(["heathrow", "stornoway"]);

        // Populated easting and northing prove mcp-server reached api, and api reached spatial-api.
        stations.Should().OnlyContain(station => station.Easting > 0 && station.Northing > 0);
    }

    [DeploymentFact]
    public async Task SummariseAFullDecadeOfHistoricData()
    {
        var result = await CallAsync(
            "summarise_station_historic_data",
            new Dictionary<string, object?>
            {
                ["stationKey"] = "heathrow",
                ["fromYear"] = 1950,
                ["toYear"] = 1960,
            }
        );

        var summary = Deserialise<SummarisedWeatherStationHistoricDataDto>(result);
        summary.FromYear.Should().Be(1950);
        summary.ToYear.Should().Be(1960);

        // Eleven complete years, so both figures are twelve times eleven. They differ only when the
        // station has gaps, which is exactly what the coverage object exists to report.
        summary.Coverage.MonthsExpected.Should().Be(132);
        summary.Coverage.MonthsReturned.Should().Be(132);
    }

    [DeploymentFact]
    public async Task ReturnMonthlyRowsForAYearRange()
    {
        var result = await CallAsync(
            "get_station_historic_data",
            new Dictionary<string, object?>
            {
                ["stationKey"] = "heathrow",
                ["fromYear"] = 1950,
                ["toYear"] = 1960,
            }
        );

        result.IsError.Should().NotBe(true, "the tool said: {0}", TextOf(result));
        Deserialise<WeatherStationHistoricDataDto>(result)
            .StationHistoricData.Should()
            .HaveCount(132)
            .And.OnlyContain(row => row.Year >= 1950 && row.Year <= 1960);
    }

    /// <summary>
    /// The unfiltered range: both years null. Worth its own test because it used to fail - the client
    /// interpolated the nulls into the query string, and the weather API answers an empty
    /// <c>?fromYear=&amp;toYear=</c> with 400. The parameters are omitted now.
    /// </summary>
    [DeploymentFact]
    public async Task ReturnEveryReadingWhenNoYearFilterIsGiven()
    {
        var result = await CallAsync(
            "get_station_historic_data",
            new Dictionary<string, object?>
            {
                ["stationKey"] = "heathrow",
                // Nullable but with no default, so the generated schema still marks them required:
                // omitting them fails the call, and explicit nulls are how an unbounded range is asked for.
                ["fromYear"] = null,
                ["toYear"] = null,
            }
        );

        result.IsError.Should().NotBe(true, "the tool said: {0}", TextOf(result));
        Deserialise<WeatherStationHistoricDataDto>(result)
            .StationHistoricData.Should()
            .HaveCountGreaterThan(132, "an unfiltered range covers far more than the eleven years above");
    }

    /// <summary>
    /// A failure the tool raises itself keeps its detail: the message names the offending parameters and
    /// the values passed to them, which is the only thing that lets a calling model correct its own
    /// request. The SDK prefixes it with "An error occurred invoking '&lt;tool&gt;'" rather than
    /// replacing it, so the assertion is on the detail surviving, not on the prefix being absent.
    /// </summary>
    [DeploymentFact]
    public async Task ExplainAnInvertedYearRangeInTheErrorItself()
    {
        var result = await CallAsync(
            "get_station_historic_data",
            new Dictionary<string, object?>
            {
                ["stationKey"] = "heathrow",
                ["fromYear"] = 1960,
                ["toYear"] = 1950,
            }
        );

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("fromYear (1960)").And.Contain("toYear (1950)");
    }

    /// <summary>
    /// An unknown station key is answered by <c>api</c> rather than caught in the tool, so this is the
    /// path where the weather API's own ProblemDetails has to survive back to the caller.
    /// </summary>
    [DeploymentFact]
    public async Task ReportAnUnknownStationKey()
    {
        var result = await CallAsync(
            "get_station_historic_data",
            new Dictionary<string, object?>
            {
                ["stationKey"] = "not-a-station",
                ["fromYear"] = 1950,
                ["toYear"] = 1960,
            }
        );

        result.IsError.Should().BeTrue();
        TextOf(result).Should().Contain("list_weather_stations", "the message should say how to find a valid key");
    }

    private async Task<CallToolResult> CallAsync(string toolName, IReadOnlyDictionary<string, object?> arguments) =>
        await Client.CallToolAsync(toolName, arguments, cancellationToken: TestContext.Current.CancellationToken);

    private static T Deserialise<T>(CallToolResult result)
        where T : class
    {
        var structuredContent =
            result.StructuredContent
            ?? throw new InvalidOperationException($"Tool returned no structured content: {TextOf(result)}");

        return structuredContent.Deserialize<T>(StructuredContentOptions)
            ?? throw new InvalidOperationException($"Structured content did not deserialise: {structuredContent}");
    }

    private static string TextOf(CallToolResult result) =>
        string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
