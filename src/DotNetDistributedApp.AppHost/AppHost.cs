using DotNetDistributedApp.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var apiDatabaseServer = builder.AddPostgres("api-database-server").WithDataVolume(isReadOnly: false);
apiDatabaseServer.WithPgAdmin(configureContainer =>
{
    configureContainer.WithExplicitStart();
});
var apiDatabase = apiDatabaseServer.AddDatabase("api-database");

var apiDatabaseMigrations = builder
    .AddProject<Projects.DotNetDistributedApp_Api_Data_MigrationService>("api-database-migrations")
    .WithReference(apiDatabase)
    .WithParentRelationship(apiDatabase)
    .WaitFor(apiDatabase);

var cache = builder
    .AddValkey("cache")
    .WithRedisInsightForValkey(configureContainer =>
    {
        configureContainer.WithExplicitStart();
    });

var geoip = builder
    .AddContainer("geoip-api", "observabilitystack/geoip-api")
    .WithHttpEndpoint(targetPort: 8080, name: "http");
var geoipEndpoint = geoip.GetEndpoint("http");

var spatialApi = builder
    .AddProject<Projects.DotNetDistributedApp_SpatialApi>("spatial-api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .WithUrlForEndpoint(
        "https",
        url =>
        {
            url.DisplayText = "Swagger UI";
            url.Url = "/swagger";
        }
    );

// WithExplicitStart() doesn't work when starting from the Dashboard. See https://github.com/dotnet/aspire/issues/12516
// .WithKafkaUI(configureContainer => { configureContainer.WithExplicitStart(); });
var events = builder.AddKafka("events").WithKafkaUI();

var api = builder
    .AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("http", url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .WithUrlForEndpoint(
        "https",
        url =>
        {
            url.DisplayText = "Swagger UI";
            url.Url = "/swagger";
        }
    )
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
