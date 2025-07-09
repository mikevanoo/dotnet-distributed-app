var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithUrlForEndpoint("https", url =>
    {
        url.DisplayText = "Swagger UI";
        url.Url = "/swagger";
    });;

builder.Build().Run();
