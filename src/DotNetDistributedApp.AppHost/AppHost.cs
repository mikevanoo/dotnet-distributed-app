var builder = DistributedApplication.CreateBuilder(args);

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

var apiDatabase = builder
    .AddPostgres("api-database-server")
    .WithDataVolume(isReadOnly: false)
    .WithPgAdmin()
    .AddDatabase("api-database");

var apiDatabaseMigrations = builder
    .AddProject<Projects.DotNetDistributedApp_Api_Data_MigrationService>("api-database-migrations")
    .WithReference(apiDatabase)
    .WaitFor(apiDatabase);

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
