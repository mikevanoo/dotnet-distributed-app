namespace DotNetDistributedApp.ServiceDefaults;

public static class ResourceNames
{
    public const string ApiDatabaseServer = "api-database-server";
    public const string ApiDatabase = "api-database";
    public const string ApiDatabaseMigrations = "api-database-migrations";
    public const string Cache = "cache";
    public const string GeoIpApi = "geoip-api";
    public const string SpatialApi = "spatial-api";
    public const string Events = "events";
    public const string EventsConsumer = "events-consumer";
    public const string Api = "api";
    public const string ScheduledTasks = "scheduled-tasks";
    public const string McpServer = "mcp-server";
    public const string McpInspector = "mcp-inspector";

    // Publish-time only: the Kubernetes compute environment and the volume backing the database.
    public const string KubernetesEnvironment = "k8s";
    public const string ApiDatabaseData = "api-database-data";
    public const string ContainerRegistry = "container-registry";
}
