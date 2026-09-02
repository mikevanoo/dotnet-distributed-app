using DotNetDistributedApp.ServiceDefaults;
using Npgsql;

namespace DotNetDistributedApp.DeploymentTests.Infrastructure;

/// <summary>
/// Reads the transactional inbox out of the deployed database over a port forward.
/// </summary>
/// <remarks>
/// <para>
/// Raw SQL rather than <c>WeatherDbContext</c>: the only question asked of the table is how many rows
/// exist per event name, and going through EF Core would mean reproducing the snake_case naming
/// convention and connection configuration here just to ask it.
/// </para>
/// <para>
/// The password comes from the release secret rather than configuration - it is generated at deploy
/// time and is not knowable in advance.
/// </para>
/// </remarks>
public sealed class DeployedInbox : IAsyncDisposable
{
    private const string PasswordSecretKey = "POSTGRES_PASSWORD";

    private readonly PortForward _portForward;
    private readonly string _connectionString;

    private DeployedInbox(PortForward portForward, string connectionString)
    {
        _portForward = portForward;
        _connectionString = connectionString;
    }

    public static async Task<DeployedInbox> ConnectAsync(
        ClusterFixture clusterFixture,
        CancellationToken cancellationToken
    )
    {
        var password = await clusterFixture.GetSecretValueAsync(
            $"{ResourceNames.ApiDatabaseServer}-secrets",
            PasswordSecretKey,
            cancellationToken
        );
        var portForward = await clusterFixture.ForwardAsync(ResourceNames.ApiDatabaseServer, 5432, cancellationToken);
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = portForward.LocalPort,
            Username = "postgres",
            Password = password,
            Database = ResourceNames.ApiDatabase,
            Timeout = 15,
        }.ConnectionString;

        return new DeployedInbox(portForward, connectionString);
    }

    /// <summary>
    /// How many rows the inbox holds for one event name, across every consumer group. Not scoped to a
    /// group on purpose: the assertions here are about deltas, and scoping would mean knowing which
    /// group the deployed consumer is running under.
    /// </summary>
    public async Task<long> CountAsync(string eventName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "select count(*) from processed_weather_events where event_name = @eventName",
            connection
        );
        command.Parameters.AddWithValue("eventName", eventName);

        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));
        await _portForward.DisposeAsync();
    }
}
