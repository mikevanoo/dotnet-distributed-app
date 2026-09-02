using System.Net.Sockets;
using k8s;

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>
/// A local TCP port that forwards to a port on a pod, for the services the chart publishes as
/// <c>ClusterIP</c> - <c>spatial-api</c>, <c>mcp-server</c>, the dashboard and Postgres.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>kubectl port-forward</c> done in-process: a <see cref="TcpListener" /> on loopback, and
/// one WebSocket to the API server per accepted connection. Spawning <c>kubectl</c> instead would work,
/// but a forwarder that outlives its test holds a dead connection open and there is no reliable way to
/// reap it on Windows - the process here dies with the test.
/// </para>
/// <para>
/// The local port is ephemeral (bind to 0, ask the OS what it got) so that parallel forwards and
/// anything already listening on the well-known ports cannot collide. That costs one thing worth
/// knowing: the dashboard's login URL names the port the pod sees, so it has to be rewritten rather
/// than used as-is. See <c>AspireDashboardShould</c>.
/// </para>
/// </remarks>
public sealed class PortForward : IAsyncDisposable
{
    private const string PortForwardSubProtocol = "v4.channel.k8s.io";
    private const int BufferSize = 16 * 1024;

    private readonly IKubernetes _client;
    private readonly string _podName;
    private readonly string _podNamespace;
    private readonly int _remotePort;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;

    private PortForward(IKubernetes client, string podNamespace, string podName, int remotePort)
    {
        _client = client;
        _podNamespace = podNamespace;
        _podName = podName;
        _remotePort = remotePort;

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptConnectionsAsync(_shutdown.Token);
    }

    public int LocalPort { get; }

    /// <summary>
    /// The first error a forwarded connection hit, if any. A forward that never connects otherwise makes
    /// its caller time out with no clue why, and this is the only place the reason exists.
    /// </summary>
    public Exception? LastError { get; private set; }

    public Uri BaseAddress => new($"http://127.0.0.1:{LocalPort}");

    public static PortForward Start(IKubernetes client, string podNamespace, string podName, int remotePort) =>
        new(client, podNamespace, podName, remotePort);

    /// <summary>An <see cref="HttpClient" /> whose base address is the forwarded port.</summary>
    public HttpClient CreateHttpClient(TimeSpan timeout) => new() { BaseAddress = BaseAddress, Timeout = timeout };

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
            // Expected: cancelling the accept loop is how the forwarder is stopped.
        }

        _shutdown.Dispose();
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        var connections = new List<Task>();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await _listener.AcceptTcpClientAsync(cancellationToken);
                connections.Add(ForwardConnectionAsync(connection, cancellationToken));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            // The listener was stopped by DisposeAsync.
        }

        await Task.WhenAll(connections);
    }

    private async Task ForwardConnectionAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        using (connection)
        {
            try
            {
                using var webSocket = await _client.WebSocketNamespacedPodPortForwardAsync(
                    _podName,
                    _podNamespace,
                    [_remotePort],
                    PortForwardSubProtocol,
                    cancellationToken: cancellationToken
                );
                using var demuxer = new StreamDemuxer(webSocket, StreamType.PortForward);
                demuxer.Start();

                // Channel 0 carries data for the first requested port; channel 1 carries its errors.
                using var podStream = demuxer.GetStream((byte?)0, (byte?)0);
                using var localStream = connection.GetStream();

                // The pumps below block in a synchronous Read, so shutdown has to break the socket
                // rather than politely ask it to stop.
                await using var abortOnShutdown = cancellationToken.Register(() =>
                {
                    webSocket.Abort();
                    connection.Close();
                });

                // Either direction closing ends the session, exactly as a proxied TCP connection should.
                await Task.WhenAny(
                    Task.Run(() => Pump(localStream, podStream), CancellationToken.None),
                    Task.Run(() => Pump(podStream, localStream), CancellationToken.None)
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A client that hangs up mid-copy is normal, so this cannot fail the test on its own.
                LastError ??= exception;
            }
        }
    }

    /// <summary>
    /// Copies one direction of the connection using <b>synchronous</b> reads and writes on a dedicated
    /// thread. This is not a style choice and must not be "modernised" to <c>CopyToAsync</c>.
    /// </summary>
    /// <remarks>
    /// KubernetesClient's demuxed stream does not override <see cref="Stream.ReadAsync(Memory{byte},
    /// CancellationToken)" /> or <see cref="Stream.WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"
    /// />, so it inherits <see cref="Stream" />'s default implementations - and those share a single
    /// semaphore across reads *and* writes. A proxy always has a read pending on the pod stream, so that
    /// read holds the semaphore and every subsequent write to the same stream waits behind it forever.
    /// The symptom is a forward that carries exactly one exchange and then hangs: an HTTP GET works and
    /// looks like proof the forward is fine, while anything conversational (Postgres startup, a TLS
    /// handshake, HTTP keep-alive) stalls on its second message with no error on either side. The
    /// synchronous methods are overridden properly and take no such lock.
    /// </remarks>
    private static void Pump(Stream from, Stream to)
    {
        var buffer = new byte[BufferSize];

        while (true)
        {
            int read;

            try
            {
                read = from.Read(buffer, 0, buffer.Length);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            try
            {
                to.Write(buffer, 0, read);
                to.Flush();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }
        }
    }
}
