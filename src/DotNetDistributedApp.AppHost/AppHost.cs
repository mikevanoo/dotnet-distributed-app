using Aspire.Hosting.Kubernetes;
using DotNetDistributedApp.AppHost;
using DotNetDistributedApp.ServiceDefaults;

var builder = DistributedApplication.CreateBuilder(args);

// Compute environment used by `aspire publish` / `aspire deploy`. It is inert during `aspire run`,
// so the local inner loop is unchanged.
// `aspire deploy` always pushes images, even when the cluster shares a container runtime with the
// host and could resolve them locally, so a registry is required. Locally this is a `registry:3`
// container published on port 5000, reachable as `localhost:5000` both from the host (push) and from
// inside the Rancher Desktop VM where the k3s dockerd pulls (pull). On AKS this becomes the ACR that
// AddAzureKubernetesEnvironment provisions.
// Suppressed to allow use of the experimental container registry APIs.
#pragma warning disable ASPIRECOMPUTE003
var containerRegistry = builder.AddContainerRegistry(ResourceNames.ContainerRegistry, "localhost:5000");

var kubernetes = builder
    .AddKubernetesEnvironment(ResourceNames.KubernetesEnvironment)
    .WithContainerRegistry(containerRegistry)
#pragma warning restore ASPIRECOMPUTE003
    .WithHelm(helm =>
        helm.WithChartName("dotnet-distributed-app")
            .WithChartVersion("0.1.0")
            .WithChartDescription("Aspire distributed application demonstrating observable, resilient microservices.")
            .WithReleaseName("dotnet-distributed-app")
            .WithNamespace("dotnet-distributed-app")
    );

// Without this the database lands on an emptyDir and every pod restart wipes it. `local-path` is the
// default storage class on Rancher Desktop's k3s; on AKS this becomes `managed-csi`.
// Suppressed to allow use of the experimental PersistentVolumeAccessMode enum.
#pragma warning disable ASPIRECOMPUTE002
var apiDatabaseData = kubernetes
    .AddPersistentVolume(ResourceNames.ApiDatabaseData)
    .WithStorageClass("local-path")
    .WithCapacity("2Gi")
    .WithAccessMode(PersistentVolumeAccessMode.ReadWriteOnce);
#pragma warning restore ASPIRECOMPUTE002

var apiDatabaseServer = builder
    .AddPostgres(ResourceNames.ApiDatabaseServer)
    .WithDataVolume(ResourceNames.ApiDatabaseData, isReadOnly: false)
    .WithPersistentVolume(apiDatabaseData);
apiDatabaseServer.WithPgAdmin(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(apiDatabaseServer);
    configureContainer.ExcludeFromManifest();
});
var apiDatabase = apiDatabaseServer.AddDatabase(ResourceNames.ApiDatabase);

var apiDatabaseMigrations = builder
    .AddProject<Projects.DotNetDistributedApp_Api_Data_MigrationService>(ResourceNames.ApiDatabaseMigrations)
    .WithReference(apiDatabase)
    .WithParentRelationship(apiDatabase)
    .WaitFor(apiDatabase)
    // This service migrates and exits, so it must not be published as a Deployment. As a pre-upgrade
    // Helm hook the Job also gates a redeploy: if it fails, no workload is updated.
    .PublishAsKubernetesJob();

var cache = builder.AddValkey(ResourceNames.Cache);
cache.WithRedisInsightForValkey(configureContainer =>
{
    configureContainer.WithExplicitStart();
    configureContainer.WithParentRelationship(cache);
});

var geoip = builder
    .AddContainer(ResourceNames.GeoIpApi, "observabilitystack/geoip-api", "2026-35")
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
    configureContainer.ExcludeFromManifest();
});

var eventsConsumer = builder
    .AddProject<Projects.DotNetDistributedApp_Events_Consumer>(ResourceNames.EventsConsumer)
    .WithReference(events)
    .WaitFor(events)
    .WithReference(apiDatabase)
    .WaitForCompletion(apiDatabaseMigrations)
    .PinToSingleReplica();

var api = builder
    .AddProject<Projects.DotNetDistributedApp_Api>(ResourceNames.Api)
    .WithExternalHttpEndpoints()
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
    .WaitFor(api)
    .PinToSingleReplica();

var mcpInspector = builder
    // >= v0.17.5 is needed for the "[ERR_INVALID_STATE]: Invalid state: Controller is already closed" fix.
    // See https://github.com/modelcontextprotocol/inspector/pull/941
    .AddMcpInspector(ResourceNames.McpInspector, options => options.InspectorVersion = "0.17.5")
    .WithMcpServer(mcpServer)
    .WithParentRelationship(mcpServer)
    .WithExplicitStart()
    .ExcludeFromManifest();

kubernetes.AddIngress("ingress").WithIngressClass("traefik").WithDefaultBackend(api.GetEndpoint("http"));

var scheduledTasks = builder
    .AddProject<Projects.DotNetDistributedApp_ScheduledTasks>(ResourceNames.ScheduledTasks)
    .WithReference(apiDatabase)
    .WaitForCompletion(apiDatabaseMigrations)
    .PinToSingleReplica();

builder.Build().Run();
