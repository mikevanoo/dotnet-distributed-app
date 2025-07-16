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
    .AddRedis("cache")
    .WithRedisInsight(configureContainer =>
    {
        configureContainer.WithExplicitStart();
    });

var spatialApi = builder
    .AddProject<Projects.DotNetDistributedApp_SpatialApi>("spatial-api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint(
        "https",
        url =>
        {
            url.DisplayText = "Swagger UI";
            url.Url = "/swagger";
        }
    );

var api = builder
    .AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithHttpHealthCheck("/health")
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
    .WaitFor(spatialApi);

builder.Build().Run();
