using DotNetDistributedApp.AppHost;
using DotNetDistributedApp.ServiceDefaults;

var builder = DistributedApplication.CreateBuilder(args);

var apiDatabaseServer = builder.AddPostgres(ResourceNames.ApiDatabaseServer).WithDataVolume(isReadOnly: false);
apiDatabaseServer.WithPgAdmin(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(apiDatabaseServer);
});
var apiDatabase = apiDatabaseServer.AddDatabase(ResourceNames.ApiDatabase);

var apiDatabaseMigrations = builder
    .AddProject<Projects.DotNetDistributedApp_Api_Data_MigrationService>(ResourceNames.ApiDatabaseMigrations)
    .WithReference(apiDatabase)
    .WithParentRelationship(apiDatabase)
    .WaitFor(apiDatabase);

var cache = builder.AddValkey(ResourceNames.Cache);
cache.WithRedisInsightForValkey(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(cache);
});

var geoip = builder
    .AddContainer(ResourceNames.GeoIpApi, "observabilitystack/geoip-api")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithUrl("/8.8.8.8", "Test for 8.8.8.8");
var geoipEndpoint = geoip.GetEndpoint("http");

var spatialApi = builder
    .AddProject<Projects.DotNetDistributedApp_SpatialApi>(ResourceNames.SpatialApi)
    .WithHttpHealthCheck("/health")
    .WithUrl("/scalar", "API UI");

var events = builder.AddKafka(ResourceNames.Events);
events.WithKafkaUI(configureContainer =>
{
    configureContainer.WithParentRelationship(events);
    configureContainer.WithExplicitStart();
});

var eventsConsumer = builder
    .AddProject<Projects.DotNetDistributedApp_Events_Consumer>(ResourceNames.EventsConsumer)
    .WithReference(events)
    .WaitFor(events)
    .WithReference(apiDatabase)
    .WaitForCompletion(apiDatabaseMigrations);

var api = builder
    .AddProject<Projects.DotNetDistributedApp_Api>(ResourceNames.Api)
    .WithHttpHealthCheck("/health")
    .WithUrls(ctx =>
    {
        var baseUrl = string.Empty;
        var url = ctx.Urls.FirstOrDefault()?.Url;
        if (url is not null)
        {
            var uri = new Uri(url);
            baseUrl = $"{uri.Scheme}://{uri.Authority}";
            ctx.Urls.Clear();
        }
        ctx.Urls.Add(new ResourceUrlAnnotation { Url = $"{baseUrl}/scalar", DisplayText = "API UI" });
        ctx.Urls.Add(new ResourceUrlAnnotation { Url = $"{baseUrl}/scalar/geoip-api", DisplayText = "GeoIP API UI" });
    })
    .WithReference(apiDatabase)
    .WaitForCompletion(apiDatabaseMigrations)
    .WithReference(spatialApi)
    .WaitFor(spatialApi)
    .WithReference(geoipEndpoint)
    .WaitFor(geoip)
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(events)
    .WaitFor(events);

#pragma warning disable ASPIREMCP001 (suppress to allow use of experimental WithMcpServer() call)
var mcpServer = builder
    .AddProject<Projects.DotNetDistributedApp_McpServer>(ResourceNames.McpServer)
    .WithMcpServer()
#pragma warning restore ASPIREMCP001
    .WithHttpHealthCheck("/health")
    .WithReference(api)
    .WaitFor(api);

var mcpInspector = builder
    // >= v0.17.5 is needed for the "[ERR_INVALID_STATE]: Invalid state: Controller is already closed" fix.
    // See https://github.com/modelcontextprotocol/inspector/pull/941
    .AddMcpInspector(ResourceNames.McpInspector, options => options.InspectorVersion = "0.17.5")
    .WithMcpServer(mcpServer)
    .WithParentRelationship(mcpServer)
    .WithExplicitStart();

var scheduledTasks = builder
    .AddProject<Projects.DotNetDistributedApp_ScheduledTasks>(ResourceNames.ScheduledTasks)
    .WithReference(apiDatabase)
    .WaitForCompletion(apiDatabaseMigrations);

builder.Build().Run();
