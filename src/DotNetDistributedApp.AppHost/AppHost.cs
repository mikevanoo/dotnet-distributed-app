var builder = DistributedApplication.CreateBuilder(args);

var apiDatabase = builder
    .AddPostgres("api-database-server")
    .WithPgAdmin()
    .AddDatabase("api-database");

var apiDatabaseMigrations = builder.AddProject<Projects.DotNetDistributedApp_Data_MigrationService>("api-database-migrations")
    .WithReference(apiDatabase)
    .WaitFor(apiDatabase);

var api = builder.AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("https", url =>
    {
        url.DisplayText = "Swagger UI";
        url.Url = "/swagger";
    })
    .WithReference(apiDatabase)
    .WithReference(apiDatabaseMigrations)
    .WaitForCompletion(apiDatabaseMigrations);

builder.Build().Run();
