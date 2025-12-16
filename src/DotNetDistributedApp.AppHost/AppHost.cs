using DotNetDistributedApp.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var apiDatabaseServer = builder.AddPostgres("api-database-server").WithDataVolume(isReadOnly: false);
apiDatabaseServer.WithPgAdmin(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(apiDatabaseServer);
});
var apiDatabase = apiDatabaseServer.AddDatabase("api-database");

var apiDatabaseMigrations = builder
    .AddProject<Projects.DotNetDistributedApp_Api_Data_MigrationService>("api-database-migrations")
    .WithReference(apiDatabase)
    .WithParentRelationship(apiDatabase)
    .WaitFor(apiDatabase);

var cache = builder.AddValkey("cache");
cache.WithRedisInsightForValkey(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(cache);
});

var geoip = builder
    .AddContainer("geoip-api", "observabilitystack/geoip-api")
    .WithHttpEndpoint(targetPort: 8080, name: "http")
    .WithUrl("/8.8.8.8", "Test for 8.8.8.8");
var geoipEndpoint = geoip.GetEndpoint("http");

var spatialApi = builder
    .AddProject<Projects.DotNetDistributedApp_SpatialApi>("spatial-api")
    .WithHttpHealthCheck("/health")
    .WithUrl("/swagger", "Swagger UI");

var events = builder.AddKafka("events");
events.WithKafkaUI(configureContainer =>
{
    configureContainer.WithParentRelationship(events);
    configureContainer.WithExplicitStart();
});

var topicInitializer = builder
    .AddProject<Projects.DotNetDistributedApp_Events_TopicInitialiser>("topic-initializer")
    .WithParentRelationship(events)
    .WithReference(events)
    .WaitFor(events);

var eventsConsumer = builder
    .AddProject<Projects.DotNetDistributedApp_Events_Consumer>("events-consumer")
    .WithReference(events)
    .WaitForCompletion(topicInitializer);

var api = builder
    .AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithUrl("/swagger", "Swagger UI")
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
