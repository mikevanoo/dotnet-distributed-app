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
    .WaitFor(events);

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
    .WithReference(apiDatabaseMigrations)
    .WaitForCompletion(apiDatabaseMigrations)
    .WithReference(spatialApi)
    .WaitFor(spatialApi)
    .WithReference(geoipEndpoint)
    .WaitFor(geoip)
    .WithReference(cache)
    .WaitFor(cache)
    .WithReference(events)
    .WaitFor(events);

builder.Build().Run();
