# Deployment plan: local Kubernetes first, then AKS

This is a design record, not a task list. It maps every resource in the Aspire app model to what it becomes when deployed, states what each one needs, and records the constraints that will break a first deployment. It deliberately stops short of prescribing code: the decisions are the point.

## Context

The solution is an Aspire **13.5.2** app model (`src/DotNetDistributedApp.AppHost/AppHost.cs`) with 6 project resources, 3 infrastructure resources and 1 third-party container, plus 4 dev-only tools. Today it only runs locally under `aspire run` — there is **no deployment content of any kind** in the repo: no `azure.yaml`, no bicep, no Dockerfile, no Helm chart, no deploy workflow.

`README.md` has three unticked rows that between them describe this piece of work:

| | |
|---|---|
| Deployment: easily deployed to the Cloud | *Aspire + azd to Azure Container Apps* |
| Deployment: scales horizontally | *Aspire and Azure Container Apps (or Kubernetes) does this* |
| Deployment: cloud-provider agnostic | *Aspire + Aspir8 to Kubernetes* |

All three notes have aged. **Aspir8 is no longer needed** — Aspire 13.3+ ships a first-party Kubernetes compute integration (`Aspire.Hosting.Kubernetes`) that generates Helm charts from the app model and deploys them. **`azd` is no longer the entry point** — `aspire deploy` now owns the provision → build → push → deploy pipeline directly. And the cloud target here is **AKS, not Container Apps**, which makes the second row largely a matter of getting the replica counts right rather than a platform question.

### The chosen route

Two stages, in this order:

1. **Local Kubernetes** on Rancher Desktop, with an **emulator standing in for Kafka**.
2. **Azure Kubernetes Service**, with Azure PaaS behind the data resources.

Both target a **demo/showcase** standard, not production: cost and simplicity over HA.

Choosing AKS over Container Apps for stage 2 makes the two stages **the same mechanism**. Both produce a Helm chart from the same app model; both use `Deployment`, `StatefulSet`, `Service`, `Job` and `PersistentVolumeClaim`; both take the same `.PublishAsKubernetesService(...)` customisations. Stage 1 stops being a rehearsal and becomes the first environment. What actually differs between them is narrow and enumerable: where images come from, what backs the data resources, how ingress and TLS are wired, and how telemetry leaves the cluster.

The Kafka emulator choice compounds that. The emulator is the **Azure Event Hubs emulator**, which speaks the Kafka protocol — so stage 1 also exercises the same client configuration and the same three code changes that Event Hubs will need. Kafka is the highest-risk part of this migration; doing it first and locally is the cheaper ordering.

---

## The app model as it stands

```
api-database-server (AddPostgres, WithDataVolume)
└── api-database (AddDatabase)
    ├── api-database-migrations   run-once worker, gates the three below
    ├── api                       HTTP
    ├── events-consumer           no HTTP
    └── scheduled-tasks           no HTTP

events (AddKafka)      → api, events-consumer
cache  (AddValkey)     → api
geoip-api (AddContainer "observabilitystack/geoip-api")  → api
spatial-api (project, HTTP)  → api
api → mcp-server (HTTP)

dev-only, WithExplicitStart: pgadmin, redisinsight, kafka-ui, mcp-inspector
```

Nothing in the model is publish-aware: no compute environment, no `WithExternalHttpEndpoints`, no `WithReplicas`, no `AddParameter`, no `PublishAsX`.

---

## Resource-by-resource mapping

Because both stages are Kubernetes, the **workload shape is identical in each column**. The two columns differ only where they have to.

| Aspire resource | Workload (both stages) | Local k8s (Rancher Desktop) | AKS | What it needs |
|---|---|---|---|---|
| `api` | `Deployment` + `Service` | Traefik ingress | AGC gateway route, `WithExternalHttpEndpoints()` | The only resource that should be internet-facing. Needs Postgres, cache, Kafka, and internal reach to `spatial-api` + `geoip-api`. Stateless → scales freely. |
| `spatial-api` | `Deployment` + `Service` | `ClusterIP` | `ClusterIP` | No dependencies at all, pure CPU (`DotSpatial.Projections`). Watch image size — the projections data is embedded. |
| `mcp-server` | `Deployment` + `Service`, **1 replica** | `ClusterIP`, or Traefik if a client needs it | AGC route + session affinity, or stay at 1 replica | `WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.StatefulForInitializeClients)` keeps MCP sessions **in process memory**. Multi-replica without affinity breaks clients. SSE streaming also needs response buffering off and a long idle timeout on whatever fronts it. |
| `events-consumer` | `Deployment`, **no Service**, `replicas: 1` | — | — | Topic `common` is created with **1 partition**, group `events-consumer` → a second replica gets no partitions and idles. Concurrency comes from `WithWorkersCount(3)` instead. Do not put an HPA on this. |
| `scheduled-tasks` | `Deployment`, no Service, `replicas: 1` | — | — | Coravel cron is `* * * * *` and `PreventOverlapping` uses an **in-process** mutex. Two replicas both fire every minute. Hard-pin to 1. |
| `api-database-migrations` | **`Job`**, `restartPolicy: OnFailure` | Helm `post-install`/`post-upgrade` hook | same | Run-once: migrates then calls `StopApplication()`. Must be single-instance (`__efmigrationshistory` races). See the ordering section — `WaitForCompletion` will not do this once deployed. |
| `api-database` / `api-database-server` | `StatefulSet` + PVC *(local only)* | `StatefulSet` + PVC on `local-path` | **Azure Database for PostgreSQL Flexible Server**, Burstable B1ms | The only durable store. On AKS this leaves the cluster entirely — no StatefulSet, no PVC, just a connection string. |
| `cache` | `Deployment` *(local only)* | Valkey container, no PVC | **Azure Managed Redis**, smallest SKU | Cache-only — HybridCache L2 + output cache. No persistence required; a cold cache is a non-event. The client already uses `Aspire.StackExchange.Redis.*`, so the Valkey → Redis swap is config-only. |
| `events` | *(differs)* | Event Hubs emulator + Azurite, as containers | **Azure Event Hubs**, Kafka endpoint, Standard tier | The one resource with no drop-in equivalent. Details below — this is where the real work is. |
| `geoip-api` | `Deployment` + `Service` | `ClusterIP` | `ClusterIP` | Third-party image `observabilitystack/geoip-api`, **no tag pinned** → resolves to `:latest`. Pin it before any registry-based deploy. No licence key or MMDB volume is configured anywhere; the image relies on its bundled free data. |
| `pgadmin`, `kafka-ui`, `mcp-inspector` | should not be deployed | | | These are `WithExplicitStart()` only — **not** `ExcludeFromManifest()` — so they *will* appear in a published manifest and get deployed. `redisinsight` is correctly excluded (`ValkeyBuilderExtensions.cs:69`). |

Three resources leave the cluster on AKS (`api-database`, `cache`, `events`) and everything else keeps the shape it had locally. That is the whole delta.

---

## The migration ordering problem

This deserves its own section because it is the one place where the local app model quietly stops describing the deployed system.

`AppHost.cs` gates `api`, `events-consumer` and `scheduled-tasks` on `.WaitForCompletion(apiDatabaseMigrations)`. **That gating is run-mode only.** Once published, `WaitForCompletion` does not hold back the deployed workloads — the chart has to enforce the ordering itself.

The fix is the same in both stages: the migration resource becomes a **`Job` with `restartPolicy: OnFailure`**, via the publisher customisation hook (`.PublishAsKubernetesService(...)`) plus a Helm `post-install`/`post-upgrade` hook. Left as a plain `Deployment`, it migrates, exits 0, and Kubernetes restarts it forever.

Aspire 13 also has a first-party alternative that would remove the hand-rolled worker entirely:

```csharp
var apiMigrations = api.AddEFMigrations("api-migrations")
    .WithMigrationsProject<Projects.Data>()
    .WithReference(db).WaitFor(db)
    .PublishAsMigrationBundle(publishContainer: true);
```

`PublishAsMigrationBundle(publishContainer: true)` turns the migration into a compute resource that each environment deploys like any other container. `PublishAsMigrationScript()` additionally emits SQL into `efmigrations/` in the publish output, useful if a human should ever approve the DDL.

Weigh that against what it would cost: the current `Api.Data.MigrationService` deliberately wraps `MigrateAsync` in `CreateExecutionStrategy().ExecuteAsync(...)` with a 5-minute command timeout, so a failure does not leave a partial migration. Whether the bundle preserves that is worth confirming before swapping. Either way, the deployed ordering has to be solved, and solving it once now covers both stages.

---

## Kafka: the emulator path

The Azure Event Hubs emulator speaks the Kafka protocol on port 9092:

- Image `mcr.microsoft.com/azure-messaging/eventhubs-emulator`, plus **Azurite** as a required dependency
- `ACCEPT_EULA=Y` must be set
- Entities (topics), their **partition counts** and consumer groups are declared in a **`Config.json` mounted into the container** — they are not created at runtime
- Kafka client config: `SecurityProtocol.SaslPlaintext`, `SaslMechanism.Plain`, username `$ConnectionString`, password = the emulator connection string
- **Only producer and consumer APIs are supported** — no AdminClient

Real Event Hubs is the same shape with `SaslSsl` instead of `SaslPlaintext`, port 9093, and topics provisioned as event hubs via Bicep. Standard tier is the minimum for Kafka; Premium is recommended for full protocol compatibility.

### Three consequences for this codebase, identical in both stages

1. **`CreateTopicIfNotExists` stops working.** Both `src/DotNetDistributedApp.Api/CoreWebApplicationBuilderExtensions.cs` and `src/DotNetDistributedApp.Events.Consumer/ServiceCollectionExtensions.cs` call `.CreateTopicIfNotExists(Topics.Common, 1, 1)` / `CommonDlq`. That is an AdminClient call. `common` and `common-dlq` must instead be declared in the emulator's `Config.json`, and provisioned as event hubs on AKS.
2. **SASL settings must reach both the producer and the consumer.** The producer is built by hand from `GetConnectionString("events")` via `.WithBrokers([cs])`; the consumer likewise. Neither goes through `AddKafkaProducer`/`AddKafkaConsumer`, so nothing wires SASL automatically — `ProducerConfig` and `ConsumerConfig` both need `SecurityProtocol`/`SaslMechanism`/`SaslUsername`/`SaslPassword`.
3. **The partition count becomes a provisioning decision, not a code one.** Today `1` is hard-coded in `CreateTopicIfNotExists`, and that single partition is what caps `events-consumer` at one replica. Declaring more partitions in `Config.json` / Bicep is what unlocks consumer scale-out — and the transactional inbox already makes the resulting redelivery safe (see `docs/KAFKA-IDEMPOTENCY-PLAN.md`).

The producer config is otherwise compatible: `EnableIdempotence = true` is supported by Event Hubs. `CompressionType.Lz4` is not — Event Hubs supports gzip or none.

### The `RunAsEmulator` trap

`RunAsEmulator()` and `RunAsContainer()` are **run-mode only**. `AddAzureEventHubs("events").RunAsEmulator()` gives you the emulator under `aspire run`, but on **publish** it emits the real Azure Event Hubs resource — the emulator container is not in the chart. The same applies to `AddAzurePostgresFlexibleServer(...).RunAsContainer(...)` and `AddAzureManagedRedis(...).RunAsContainer(...)`.

Stage 1 is a *deployed* local cluster, not `aspire run`, so:

- To get the Event Hubs emulator **into the Helm chart**, model it as ordinary published resources — `AddContainer` for the emulator plus one for Azurite, with `Config.json` mounted and `ACCEPT_EULA=Y` — not via `RunAsEmulator`.
- Postgres and Valkey stay as plain `AddPostgres` / `AddValkey` containers for the local target, and only become `AddAzure*` for AKS.

So "one AppHost, two targets" is conditional on `builder.ExecutionContext` or a parameter, not just two compute-environment calls. Decide this at the start of stage 1 rather than discovering it at the start of stage 2. The simplest version: branch the **three data resources** on a deployment-target parameter and keep the six project resources identical across both branches — which is exactly the delta identified in the mapping table.

### Alternative

Run a real single-broker Kafka (or Redpanda) in both clusters instead, and skip Event Hubs entirely. Zero code change, `CreateTopicIfNotExists` keeps working, and on AKS it is a StatefulSet with a managed-disk PVC. For a demo environment that is defensible; it trades a managed service for a broker you now operate, and the three code changes above never happen.

---

## Stage 1: local Kubernetes on Rancher Desktop

Aspire's first-party Kubernetes compute integration replaces Aspir8:

- Package `Aspire.Hosting.Kubernetes` (`aspire add kubernetes`); **Helm v4.2.0+** must be on PATH
- `builder.AddKubernetesEnvironment("k8s")` declares the target; `.WithHelm(h => h.WithChartName(...).WithNamespace(...).WithReleaseName(...))` configures chart generation
- `aspire publish` emits a Helm chart to inspect; `aspire deploy` runs `helm install` against the **current kubectl context**
- Aspire's mapping: projects/containers → `Deployment` (or `StatefulSet` when bound to a volume), endpoints → `Service`, connection strings and env vars → `ConfigMap`/`Secret`, volumes → `PersistentVolume`/`PVC`
- Per-resource tuning: `.PublishAsKubernetesService(r => { if (r.Workload is Deployment d) d.Spec.Replicas = 1; })`
- Persistent volumes: `k8s.AddPersistentVolume("pg-data").WithStorageClass(...).WithCapacity(...)`, then `.WithPersistentVolume(pgData)` on the Postgres resource
- Container registry APIs are **preview** and need `#pragma warning disable ASPIRECOMPUTE003`

Everything in that list carries forward to stage 2 unchanged.

### Rancher Desktop specifics to settle first

- **Image visibility.** `AddContainerRegistry("registry", "host:port")` + `.WithContainerRegistry(registry)` expects a registry reachable from *both* the host and the cluster. The two workable options are a local `registry:2` on `localhost:5000` with a matching k3s `registries.yaml` entry, or loading images straight into k3s's containerd. **Confirm which container engine backend Rancher Desktop is set to** (dockerd/moby vs containerd/nerdctl) before assuming a locally built image is visible to k3s — with the containerd backend, images in the `k8s.io` namespace are visible directly; with dockerd it depends on how k3s was configured. A single `kubectl run` against a locally built tag settles it.
- **Ingress.** Rancher Desktop's k3s ships Traefik. Only `api` — and `mcp-server`, if it needs to be reachable by an MCP client — needs an ingress rule; everything else stays `ClusterIP`.
- **Storage.** k3s's `local-path` provisioner covers the Postgres PVC. Set the storage class explicitly rather than relying on the default; on AKS it becomes `managed-csi`, so having it named rather than defaulted is what makes that a one-line change.
- **Capacity.** Postgres + Valkey + Event Hubs emulator + Azurite + GeoIP + 6 app pods on one node. The emulator alone wants 2 GB RAM. Give the VM headroom.

---

## Stage 2: Azure Kubernetes Service

Package `Aspire.Hosting.Azure.Kubernetes` (`aspire add azure-kubernetes`). One call declares the environment:

```csharp
var aks = builder.AddAzureKubernetesEnvironment("aks");
```

**When an AKS environment is present, every compute resource is deployed to it automatically — there is no per-resource opt-in.** That makes the dev-tools leak (blocker 3) more pressing here, not less.

`aspire deploy` then provisions the AKS cluster, an ACR, and a managed identity with `AcrPull` for credential-free image pulls, plus any Azure PaaS resources declared in the AppHost; builds and pushes images; generates the Helm chart; and installs it. `aspire publish -o aks-artifacts` produces the Helm chart **and Bicep** for review or a custom CI/CD pipeline.

Prerequisites: Azure CLI authenticated (`az login`), `kubectl`, Helm 4.2.0+, and the AGC feature registrations on the subscription if you use the gateway path.

### Node pools

```csharp
builder.AddAzureKubernetesEnvironment("aks")
    .WithSystemNodePool("Standard_D4s_v5", minCount: 1, maxCount: 5);
```

Additional pools are `aks.AddNodePool("name", sku, minCount, maxCount)`, attached per resource with `.WithNodePool(pool)`. For a demo, a single small system pool with `minCount: 1` is enough — but note the cost shape below.

### Ingress and TLS

The integrated path is Application Gateway for Containers via Gateway API:

```csharp
var vnet = builder.AddAzureVirtualNetwork("vnet", "10.100.0.0/16");
var aksSubnet = vnet.AddSubnet("aks-nodes", "10.100.0.0/22");
var albSubnet = vnet.AddSubnet("alb-public", "10.100.4.0/24");

var aks = builder.AddAzureKubernetesEnvironment("aks").WithSubnet(aksSubnet);
var publicLb = aks.AddLoadBalancer("public", albSubnet);

var api = builder.AddProject<Projects.DotNetDistributedApp_Api>("api")
    .WithExternalHttpEndpoints();

aks.AddGateway("weather")
    .WithLoadBalancer(publicLb)
    .WithRoute("/", api.GetEndpoint("http"));
```

When `AddLoadBalancer(...)` is used, Aspire provisions the AGC ingress profile, assigns the controller identity its `Network Contributor` role, and exposes Gateway API routing.

TLS via cert-manager:

```csharp
var certManager = aks.AddCertManager("cert-manager");
var letsencrypt = certManager.AddIssuer("letsencrypt-prod")
    .WithLetsEncryptProduction("ops@example.com")
    .WithHttp01Solver();

aks.AddGateway("weather")
    .WithLoadBalancer(publicLb)
    .WithHostname(builder.AddParameter("hostname"))
    .WithRoute("/", api.GetEndpoint("http"))
    .WithTls(letsencrypt);
```

Deployed with `aspire deploy --parameter hostname=weather.example.com`.

One caveat if you take the `AddIngress` route instead of `AddGateway`: **HTTP-01 challenges do not work with AGC Ingress**, because AGC creates a separate frontend per Ingress resource. Use a DNS-01 solver against Azure DNS there. The `AddGateway` path above is documented with HTTP-01.

### Data resources

`api-database` → `AddAzurePostgresFlexibleServer(...)`, `cache` → `AddAzureManagedRedis(...)`, `events` → `AddAzureEventHubs(...)`. Aspire provisions these via the generated Bicep and injects connection strings into the pods. Where you want managed identity rather than access keys, the resource itself has to permit it — a Postgres server not configured for Entra auth cannot be reached by managed identity no matter what the app model says, and a Redis with access keys disabled cannot use them. Decide the auth mode per resource when you provision, not afterwards.

### What AKS costs you relative to Container Apps

Worth being explicit, since this is a demo environment:

- **No scale-to-zero.** ACA Consumption bills nothing at zero replicas; an AKS node pool with `minCount: 1` bills continuously. `spatial-api` and `geoip-api` were the two scale-to-zero candidates and that saving is gone.
- **No managed OTel agent.** ACA has a built-in agent that forwards OTLP to Application Insights with no code change. On AKS you run an OpenTelemetry Collector in-cluster yourself, or add `Azure.Monitor.OpenTelemetry.AspNetCore` to the services. See blocker 9.
- **You own the cluster.** Node image upgrades, Kubernetes version upgrades, and capacity are yours.

What you get back is that stage 1 and stage 2 are the same system, the chart is portable off Azure, and per-resource control is direct rather than mediated by the ACA resource model. Given "cloud-provider agnostic" is one of the three README goals, that is a coherent trade.

---

## Verified against the generated chart (stage 1, increment 1)

`aspire publish` now emits a chart that renders, lints, and passes `kubectl apply --dry-run=server`
against the Rancher Desktop cluster (35 objects). Several assumptions above turned out to be wrong,
recorded here rather than silently corrected:

- **No local registry is needed.** Rancher Desktop's k3s reports its runtime as `docker://29.5.3`, and
  the publisher emits `imagePullPolicy: IfNotPresent`, so locally built images are visible to
  Kubernetes directly. The "image visibility" question in stage 1 resolves in the easy direction. Note
  the host's Windows docker CLI is a separate matter - see the open items below.
- **The publisher emits no probes at all.** Blockers 1 and 2 do not bite yet. Lifting the
  `IsDevelopment()` guard in `ServiceDefaults` is still correct - `WithHttpHealthCheck` means nothing
  without it, and probes will come - but it is not currently load-bearing.
- **`WithExternalHttpEndpoints()` does not create ingress** on the Kubernetes publisher. The `api`
  Service stayed `ClusterIP` until `kubernetes.AddIngress(...).WithIngressClass("traefik")` was added.
- **Replica pinning is currently inert.** Every workload already defaults to `replicas: 1`, so
  `PinToSingleReplica()` changes nothing in today's output. It is kept as the correct hook and as
  documentation of *why* three services cannot scale.
- **There is no Job workload type.** `Aspire.Hosting.Kubernetes` models only `Deployment` and
  `StatefulSet`, so the migration service could not be published as a Job as assumed. `JobV1` /
  `JobSpecV1` in `src/DotNetDistributedApp.AppHost/KubernetesJobResources.cs` add the missing
  `batch/v1` type, and `PublishAsKubernetesJob()` swaps the Deployment for it.
- **The migration hook is `post-install`, not `pre-install`.** Helm runs pre-install hooks before any
  release resource exists, so the Job would find neither its ConfigMap and Secret nor the database.
  Running it post-install means both exist, and Helm still waits for it. The residual cost is that
  dependent services start before the schema does; the EF Core execution strategy and
  `EnableRetryOnFailure` absorb it. This is *not* equivalent to `WithFor`/`WaitForCompletion` - gating
  deployed pods properly needs an init container on each dependent.
- **`Aspire.Hosting.Kubernetes` is preview-only.** No stable release exists; the repo is otherwise
  entirely stable packages.

Still open before a first deploy: the Windows `\\.\pipe\docker_engine` named pipe is missing, so
`aspire deploy` cannot build images from the host even though dockerd works inside the VM; and
`values.yaml` ships empty strings for every password and connection string, which `aspire deploy`
is expected to populate but `helm install` on its own would not.

---

## Blockers — these break a first deployment, in either stage

Ranked by how certainly they will bite:

1. **Health endpoints do not exist outside Development.** `ServiceDefaults/Extensions.cs:113-130` maps `/health` and `/alive` only when `app.Environment.IsDevelopment()`, while `AppHost.cs` declares `.WithHttpHealthCheck("/health")` on `api`, `spatial-api` and `mcp-server`. With `ASPNETCORE_ENVIRONMENT=Production`, every readiness probe 404s and no pod ever goes ready. Either lift the guard — the comment there is about not exposing *detail*, which is a `ResponseWriter` concern rather than an existence one — or omit the probes.
2. **`events-consumer` and `scheduled-tasks` have no HTTP server at all** — no `MapDefaultEndpoints`, no Kestrel, in any environment. They need no `readinessProbe` and no `livenessProbe`, not probes pointed elsewhere.
3. **Dev tools leak into the manifest.** pgAdmin, Kafka UI and MCP Inspector are `WithExplicitStart()` but not `ExcludeFromManifest()`, so they are published and deployed. MCP Inspector is npx/Node-backed — it will try to run in the cluster. On AKS this is unavoidable without the fix, since every compute resource is deployed automatically.
4. **`WaitForCompletion` does not survive publishing.** The migration service restart-loops as a `Deployment`, and the three services depending on it start against an unmigrated database.
5. **`CreateTopicIfNotExists`** against an emulator or Event Hubs — see above.
6. **Replica pinning.** `events-consumer`, `scheduled-tasks` and `mcp-server` each need an explicit cap, for three unrelated reasons (one partition; in-process mutex; in-memory sessions). None is expressed today, and none of them should get an HPA.
7. **Hard-coded service-discovery schemes.** `Api` uses `https://spatial-api` and `http://geoip-api`; `McpServer` uses `https://api`. None uses the `https+http://` fallback form, and `ServiceDefaults` sets `ServiceDiscoveryOptions.AllowedSchemes = ["https"]` unconditionally. Verify against Kubernetes `Service` DNS — `http://geoip-api` against an https-only allowlist is the one to check first, and it will behave the same in both clusters, so stage 1 catches it.
8. **Untagged `observabilitystack/geoip-api`.** Non-deterministic in any registry-based deploy.
9. **Telemetry goes nowhere by default.** `ServiceDefaults` registers the OTLP exporter *only* if `OTEL_EXPORTER_OTLP_ENDPOINT` is non-empty. There is no managed agent on AKS to set it for you, so this needs a real answer in both stages: run an OpenTelemetry Collector in-cluster (locally forwarding to the Aspire dashboard or Jaeger; on AKS forwarding to Application Insights), or add `Azure.Monitor.OpenTelemetry.AspNetCore` to the services and skip OTLP on Azure. The collector route keeps the code identical across both stages and is the one that fits the app as written.
10. **`api-database-migrations` is the only project that does not call `AddServiceDefaults()`.** No OTLP exporter, no Serilog, no health checks. A failed migration in a cluster surfaces as a Job in `Error` and nothing else — `kubectl logs` is your only signal.

Non-blocking but worth knowing: there are **no readiness checks against Postgres, Redis or Kafka** — the only registered check is a `self` liveness stub. Every pod reports ready while its dependencies are down, which on Kubernetes means traffic gets routed to it.

---

## Verification

Stage 1, in order:

1. `aspire publish` and read the generated chart before deploying anything. Fastest way to confirm the mapping above — in particular which workloads became StatefulSets, whether the migration resource is a `Job`, and whether the dev tools appear.
2. `helm install --dry-run` against the Rancher Desktop context.
3. Deploy, then `kubectl get pods -w`: the migration Job must reach `Completed` before `api` becomes ready.
4. `curl` the Traefik ingress for `api` → `/v1/weather/stations`. A 200 with populated station data exercises Postgres, the cache, `spatial-api` and `geoip-api` in one call. The GeoIP resilience fallback returns 204 and the API degrades to null geo data rather than failing, so check the payload, not just the status code.
5. `POST /v1/events/simple-event`, then confirm a row lands in `processed_weather_events`. That is the whole Kafka path end to end, including the inbox.
6. `POST /v1/events/failing-event` and confirm it reaches `common-dlq` after its retries.
7. Point an MCP client at `mcp-server`'s `/mcp` and call a weather tool.

Stage 2 repeats 3–7 against the AKS cluster. Steps 1 and 2 are worth repeating too, since the chart differs — the three data resources drop out and the gateway appears. The parts that should be **identical** between the two chart diffs are the six project workloads; if they are not, something has been configured on the compute environment that belongs on the resource.

The existing integration tests (`tests/DotNetDistributedApp.IntegrationTests`, `AppHostFixture`) run the AppHost via `Aspire.Hosting.Testing` against Docker. They validate the app model, not the deployment, so they remain a pre-deploy gate rather than a post-deploy check.
