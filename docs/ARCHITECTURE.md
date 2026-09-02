# Architecture

A [C4 model](https://c4model.com) of DotNetDistributedApp, described in Mermaid so that it renders
in the GitHub UI with no tooling. See [Rendering these diagrams](#rendering-these-diagrams) for how
to view them locally and for the optional interactive Structurizr model.

Levels 1-3 are here. Level 4 (code) is deliberately absent - the source is the better answer at
that zoom level.

| Level | Diagram | Answers |
|-------|---------|---------|
| 1 | [System Context](#level-1-system-context) | Who uses the system and what does it depend on? |
| 2 | [Containers](#level-2-containers) | What are the deployable pieces and how do they talk? |
| 3 | [Components: `api`](#level-3-components---api) | How is the REST API put together? |
| 3 | [Components: `events-consumer`](#level-3-components---events-consumer) | Why is the Kafka pipeline ordered the way it is? |
| 3 | [Components: `mcp-server`](#level-3-components---mcp-server) | How is the domain exposed to an LLM? |
| - | [Deployment](#deployment-kubernetes) | What does the Helm chart actually install? |

Colour convention, used consistently throughout:

```mermaid
flowchart LR
    p["Person"]
    c["Container or component<br/>inside the system"]
    d[("Data store")]
    e["External system"]

    classDef person fill:#08427b,stroke:#052e56,color:#ffffff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff

    class p person
    class c internal
    class d store
    class e external
```

---

## Level 1: System Context

The system serves UK weather-station data. It has two kinds of human user, and reaches outside
itself for IP geolocation and for telemetry.

```mermaid
flowchart TB
    apiConsumer["API Consumer<br/>[Person]<br/><br/>Explores and calls the weather and<br/>events endpoints, via the Scalar UI<br/>or any HTTP client"]
    assistantUser["Assistant User<br/>[Person]<br/><br/>Asks an LLM natural-language<br/>questions about weather stations"]

    subgraph systemBoundary["DotNetDistributedApp"]
        system["Weather Platform<br/>[Software System]<br/><br/>Serves weather-station data over REST and MCP,<br/>publishes and consumes domain events, and is<br/>orchestrated end to end by .NET Aspire"]
    end

    mcpHost["MCP Host<br/>[External Software System]<br/><br/>Claude Code, Claude Desktop or the<br/>MCP Inspector - discovers and calls<br/>the weather tools on the user's behalf"]
    geoIp["GeoIP API<br/>[External Software System]<br/><br/>observabilitystack/geoip-api - resolves an<br/>IP address to country, city and coordinates"]
    telemetry["Telemetry Backend<br/>[External Software System]<br/><br/>Receives OTLP traces, metrics and logs.<br/>The Aspire dashboard here; any OTLP<br/>collector elsewhere"]

    apiConsumer -->|"Reads weather data and<br/>publishes events<br/>[HTTPS/JSON]"| system
    assistantUser -->|"Asks questions"| mcpHost
    mcpHost -->|"Lists and calls tools<br/>[MCP over Streamable HTTP]"| system
    system -->|"Looks up IP geolocation<br/>[HTTP/JSON]"| geoIp
    system -->|"Exports traces, metrics and logs<br/>[OTLP]"| telemetry

    classDef person fill:#08427b,stroke:#052e56,color:#ffffff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884

    class apiConsumer,assistantUser person
    class system internal
    class mcpHost,geoIp,telemetry external
    class systemBoundary boundary
```

## Level 2: Containers

Every box below is a resource in `src/DotNetDistributedApp.AppHost/AppHost.cs` - that file is the
authoritative dependency graph, and this diagram is a reading of it. Names match `ResourceNames.cs`.

`geoip-api` is drawn as external because it is a third-party image the app model happens to host;
it is not code in this repository.

```mermaid
flowchart TB
    apiConsumer["API Consumer<br/>[Person]"]
    mcpHost["MCP Host<br/>[External Software System]"]
    geoIp["geoip-api<br/>[External Container: Docker]<br/><br/>observabilitystack/geoip-api.<br/>IP address to geolocation"]
    telemetry["Telemetry Backend<br/>[External Software System]<br/><br/>Aspire dashboard / OTLP collector"]

    subgraph systemBoundary["DotNetDistributedApp"]
        direction TB

        api["api<br/>[Container: ASP.NET Core Minimal API]<br/><br/>Versioned v1 and v2 weather and events<br/>endpoints. The only externally exposed<br/>container. Scalar UI at /scalar"]
        spatialApi["spatial-api<br/>[Container: ASP.NET Core Minimal API]<br/><br/>Converts WGS84 lat/long to and from<br/>OSGB36 grid references. Stateless,<br/>no dependencies of its own"]
        mcpServer["mcp-server<br/>[Container: ASP.NET Core + ModelContextProtocol]<br/><br/>Exposes the weather domain as three MCP<br/>tools. Single replica - MCP sessions are<br/>held in process memory"]

        eventsConsumer["events-consumer<br/>[Container: .NET Worker + KafkaFlow]<br/><br/>Consumes the common topic through a<br/>retry / dead-letter / metrics /<br/>transactional-inbox pipeline"]
        scheduledTasks["scheduled-tasks<br/>[Container: .NET Worker + Coravel]<br/><br/>Cron job that prunes aged rows from<br/>the processed-events inbox"]
        migrations["api-database-migrations<br/>[Container: .NET Worker, runs once]<br/><br/>Applies EF Core migrations and seed data,<br/>then exits. Every database consumer waits<br/>for it to complete"]

        database[("api-database<br/>[Container: PostgreSQL]<br/><br/>Weather stations, historic data and<br/>the processed-events inbox. snake_case,<br/>EF Core retry-on-failure")]
        cache[("cache<br/>[Container: Valkey]<br/><br/>Output cache, HybridCache backing<br/>store and distributed cache")]
        kafka[("events<br/>[Container: Apache Kafka]<br/><br/>Topics: common, common-dlq.<br/>Single partition")]
    end

    apiConsumer -->|"[HTTPS/JSON]"| api
    mcpHost -->|"[MCP over<br/>Streamable HTTP]"| mcpServer

    mcpServer -->|"Reads weather data<br/>[HTTP/JSON, Polly retry +<br/>timeout + breaker + fallback]"| api
    api -->|"Converts station coordinates<br/>[HTTP/JSON, Polly retry +<br/>timeout + breaker + fallback]"| spatialApi
    api -->|"Resolves caller geolocation,<br/>cache-aside via HybridCache<br/>[HTTP/JSON, Polly + fallback]"| geoIp
    api -->|"Reads stations and historic data<br/>[EF Core / Npgsql]"| database
    api -->|"Caches endpoint responses<br/>and GeoIP lookups<br/>[RESP]"| cache
    api -->|"Publishes domain events to common<br/>[Kafka, idempotent, acks=all]"| kafka

    kafka -->|"Delivers events from common<br/>[Kafka, group events-consumer]"| eventsConsumer
    eventsConsumer -->|"Publishes undeliverable raw<br/>messages to common-dlq<br/>[Kafka, no serializer]"| kafka
    eventsConsumer -->|"Reads and writes the inbox inside<br/>the handler transaction<br/>[EF Core / Npgsql]"| database
    scheduledTasks -->|"Deletes aged inbox rows<br/>[EF Core ExecuteDeleteAsync]"| database
    migrations -->|"Migrates schema and seeds data<br/>[EF Core]"| database

    api -.->|"[OTLP]"| telemetry
    spatialApi -.->|"[OTLP]"| telemetry
    mcpServer -.->|"[OTLP]"| telemetry
    eventsConsumer -.->|"[OTLP]"| telemetry
    scheduledTasks -.->|"[OTLP]"| telemetry

    classDef person fill:#08427b,stroke:#052e56,color:#ffffff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884

    class apiConsumer person
    class api,spatialApi,mcpServer,eventsConsumer,scheduledTasks,migrations internal
    class database,cache,kafka store
    class mcpHost,geoIp,telemetry external
    class systemBoundary boundary
```

### Startup ordering

Three containers will not start until `api-database-migrations` has exited successfully
(`.WaitForCompletion(...)`), so a broken migration fails the deploy rather than the first request.
Arrows point from a dependency to what waits on it.

```mermaid
flowchart LR
    database[("api-database")] --> migrations["api-database-migrations<br/>runs once, then exits"]
    migrations --> api["api"]
    migrations --> eventsConsumer["events-consumer"]
    migrations --> scheduledTasks["scheduled-tasks"]
    spatialApi["spatial-api"] --> api
    cache[("cache")] --> api
    kafka[("events")] --> api
    kafka --> eventsConsumer
    geoIp["geoip-api"] --> api
    api --> mcpServer["mcp-server"]

    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff

    class migrations,api,eventsConsumer,scheduledTasks,spatialApi,mcpServer internal
    class database,cache,kafka store
    class geoIp external
```

## Level 3: Components - `api`

Endpoints are registered by `Map*Endpoints()` extension methods and call services directly; there
is no repository, mapper or mediator layer. Every service method returns `Result<T>` and is turned
into an HTTP response by `.ToApiResponse()`.

```mermaid
flowchart TB
    apiConsumer["API Consumer<br/>[Person]"]
    mcpServer["mcp-server<br/>[Container]"]

    subgraph apiBoundary["api [Container: ASP.NET Core Minimal API]"]
        direction TB

        weatherEndpoints["Weather Endpoints<br/>[Component: Minimal API group]<br/><br/>GET /v1|/v2 weather/stations<br/>GET .../stations/:key/historic-data<br/>Output-cached 30s, varies by stationKey"]
        eventsEndpoints["Events Endpoints<br/>[Component: Minimal API group]<br/><br/>POST /v1|/v2 events/simple-event<br/>POST .../failing-event<br/>POST .../duplicate-event"]

        weatherService["WeatherService<br/>[Component]<br/><br/>Loads stations, enriches them with grid<br/>references and caller geolocation, and<br/>times the historic-data query"]
        eventsService["EventsService<br/>[Component: Api.Common]<br/><br/>Wraps the KafkaFlow producer.<br/>Shared with other containers"]

        coordinateConverterClient["CoordinateConverterClient<br/>[Component: typed HttpClient]<br/><br/>Resilience pipeline returns 204 on<br/>failure so enrichment degrades<br/>instead of failing the request"]
        geoIpClient["GeoIpClient<br/>[Component: typed HttpClient]<br/><br/>Cache-aside over HybridCache,<br/>records cache hit and miss counters"]

        dbContext["WeatherDbContext<br/>[Component: EF Core]<br/><br/>WeatherStations,<br/>WeatherStationHistoricData,<br/>ProcessedWeatherEvents"]
        metricsService["MetricsService<br/>[Component]<br/><br/>Custom counters and histograms<br/>published over OpenTelemetry"]
        serviceDefaults["ServiceDefaults<br/>[Component]<br/><br/>OTLP exporters, /health and /alive,<br/>service discovery, default resilience"]
    end

    spatialApi["spatial-api<br/>[Container]"]
    geoIp["geoip-api<br/>[External Container]"]
    database[("api-database<br/>[Container: PostgreSQL]")]
    cache[("cache<br/>[Container: Valkey]")]
    kafka[("events<br/>[Container: Kafka]")]

    apiConsumer -->|"[HTTPS/JSON]"| weatherEndpoints
    apiConsumer -->|"[HTTPS/JSON]"| eventsEndpoints
    mcpServer -->|"[HTTP/JSON]"| weatherEndpoints

    weatherEndpoints --> weatherService
    weatherEndpoints -->|"Response caching<br/>[RESP]"| cache
    eventsEndpoints --> eventsService

    weatherService --> coordinateConverterClient
    weatherService --> geoIpClient
    weatherService --> dbContext
    weatherService --> metricsService
    geoIpClient --> metricsService

    coordinateConverterClient -->|"[HTTP/JSON]"| spatialApi
    geoIpClient -->|"[HTTP/JSON]"| geoIp
    geoIpClient -->|"HybridCache L2<br/>[RESP]"| cache
    dbContext -->|"[Npgsql]"| database
    eventsService -->|"Produces to common<br/>[Kafka]"| kafka

    classDef person fill:#08427b,stroke:#052e56,color:#ffffff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef component fill:#85bbf0,stroke:#5d82a8,color:#000000
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884

    class apiConsumer person
    class mcpServer,spatialApi internal
    class weatherEndpoints,eventsEndpoints,weatherService,eventsService,coordinateConverterClient,geoIpClient,dbContext,metricsService,serviceDefaults component
    class database,cache,kafka store
    class geoIp external
    class apiBoundary boundary
```

## Level 3: Components - `events-consumer`

This is a middleware pipeline, so ordering *is* the architecture. Each position is load-bearing and
guarded by `EventsConsumerRegistrationShould`; the reasons live in
[AGENTS.md](../AGENTS.md#kafka-consumer-data-loss-constraints) and
[KAFKA-IDEMPOTENCY-PLAN.md](KAFKA-IDEMPOTENCY-PLAN.md). Read the pipeline outside-in.

```mermaid
flowchart TB
    kafka[("events, topic common<br/>[Container: Kafka]")]

    subgraph consumerBoundary["events-consumer [Container: .NET Worker + KafkaFlow]"]
        direction TB

        retryDlq["RetryDeadLetterMiddleware<br/>[Component]<br/><br/>Outermost, and deliberately outside the<br/>deserializer. Retries with backoff, then dead<br/>letters the raw bytes. Only ever sees bytes.<br/>Swallows the exception once dead lettered"]
        deserializer["JsonCoreDeserializer<br/>+ StrictMessageTypeResolver<br/>[Component]<br/><br/>Strict resolution throws on a missing or<br/>unloadable Message-Type header instead of<br/>silently dropping the message"]
        metrics["ConsumerMetricsMiddleware<br/>[Component]<br/><br/>events.consume_success / _failed /<br/>_unrecognised, tagged by event_name. Owns<br/>the 'not a BaseEventPayloadDto' decision"]
        dedup["WeatherDeduplicationMiddleware<br/>[Component, MiddlewareLifetime.Message]<br/><br/>Transactional inbox: opens a transaction,<br/>short-circuits duplicates, runs handlers inside<br/>it, records ProcessedWeatherEvent, commits"]
        typedHandlers["TypedHandlerMiddleware<br/>[Component]<br/><br/>Dispatches by payload type. Exactly one handler<br/>per type - it runs them concurrently on a<br/>shared scoped DbContext"]

        simpleHandler["SimpleEventMessageHandler<br/>[Component, scoped]"]
        failingHandler["FailingEventMessageHandler<br/>[Component, scoped]<br/><br/>Always throws - exercises retry and DLQ"]

        dlqProducer["DlqProducer<br/>[Component]<br/><br/>No serializer middleware: produces the<br/>original bytes verbatim so dead letters<br/>stay readable"]
    end

    dlqTopic[("events, topic common-dlq<br/>[Container: Kafka]")]
    database[("api-database<br/>[Container: PostgreSQL]")]

    kafka -->|"Consumes, 3 workers,<br/>group events-consumer"| retryDlq
    retryDlq --> deserializer
    deserializer --> metrics
    metrics --> dedup
    dedup --> typedHandlers
    typedHandlers --> simpleHandler
    typedHandlers --> failingHandler

    retryDlq -->|"On exhausted retries"| dlqProducer
    dlqProducer -->|"[Kafka, raw bytes]"| dlqTopic
    dedup -->|"Reads and writes<br/>ProcessedWeatherEvents<br/>[EF Core, one transaction]"| database

    classDef component fill:#85bbf0,stroke:#5d82a8,color:#000000
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884

    class retryDlq,deserializer,metrics,dedup,typedHandlers,simpleHandler,failingHandler,dlqProducer component
    class kafka,dlqTopic,database store
    class consumerBoundary boundary
```

`scheduled-tasks` exists because the inbox table doubles as an audit log:
`ProcessedWeatherEventsCleaner` is a Coravel `IInvocable` that deletes aged rows per event name,
then a catch-all for the rest. It is not scoped to a consumer group.

## Level 3: Components - `mcp-server`

The MCP server is a thin adapter: it owns no data and reaches the domain only through the public
REST API, which keeps the tool surface and the HTTP surface honest about each other.

```mermaid
flowchart TB
    mcpHost["MCP Host<br/>[External Software System]<br/><br/>Claude Code, Claude Desktop,<br/>MCP Inspector"]

    subgraph mcpBoundary["mcp-server [Container: ASP.NET Core + ModelContextProtocol.AspNetCore]"]
        direction TB
        transport["MCP HTTP Transport<br/>[Component]<br/><br/>Streamable HTTP, stateful for clients<br/>that send initialize. Sessions live<br/>in process memory"]
        weatherTools["WeatherTools<br/>[Component: McpServerToolType]<br/><br/>list_weather_stations<br/>get_station_historic_data<br/>summarise_station_historic_data"]
        summariser["WeatherStationHistoricDataSummariser<br/>[Component]<br/><br/>Aggregates a year range so the model<br/>gets a summary rather than a<br/>context-flooding row dump"]
        weatherApiClient["WeatherApiClient<br/>[Component: typed HttpClient]<br/><br/>Polly retry, timeout, breaker<br/>and a 204 fallback"]
    end

    api["api<br/>[Container]"]

    mcpHost -->|"Lists and calls tools<br/>[MCP over Streamable HTTP]"| transport
    transport --> weatherTools
    weatherTools --> summariser
    weatherTools --> weatherApiClient
    weatherApiClient -->|"GET /v1/weather/...<br/>[HTTP/JSON]"| api

    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef component fill:#85bbf0,stroke:#5d82a8,color:#000000
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884

    class api internal
    class transport,weatherTools,summariser,weatherApiClient component
    class mcpHost external
    class mcpBoundary boundary
```

## Deployment: Kubernetes

`AddKubernetesEnvironment` generates a plain Helm chart from the same app model, so this view is
derived from the container view rather than maintained separately. The node kinds below match the
rendered chart.

```mermaid
flowchart TB
    dev["Developer<br/>[Person]"]
    registry["container-registry<br/>[Node: registry:3]<br/><br/>localhost:5000 locally,<br/>ACR on AKS"]

    subgraph cluster["Kubernetes cluster"]
        direction TB
        ingress["ingress<br/>[Ingress: traefik]<br/><br/>Default backend to api"]

        subgraph namespace["namespace dotnet-distributed-app - Helm release dotnet-distributed-app"]
            direction TB

            subgraph deployments["Deployments"]
                direction TB
                apiDep["api<br/>[Deployment]<br/>Stateless - scale with replicas or an HPA"]
                spatialDep["spatial-api<br/>[Deployment]<br/>Stateless"]
                mcpDep["mcp-server<br/>[Deployment, 1 replica]<br/>In-memory MCP sessions"]
                consumerDep["events-consumer<br/>[Deployment, 1 replica]<br/>Capped by one Kafka partition"]
                tasksDep["scheduled-tasks<br/>[Deployment, 1 replica]<br/>Coravel's overlap mutex is in-process"]
                geoipDep["geoip-api<br/>[Deployment]"]
                dashboardDep["k8s-dashboard<br/>[Deployment]<br/>Aspire dashboard, OTLP sink"]
            end

            subgraph statefulsets["StatefulSets"]
                direction TB
                pgSts[("api-database-server<br/>[StatefulSet: PostgreSQL]")]
                cacheSts[("cache<br/>[StatefulSet: Valkey]")]
                kafkaSts[("events<br/>[StatefulSet: Kafka]")]
            end

            migrationJob["api-database-migrations<br/>[Job, Helm pre-upgrade hook]<br/><br/>A gate: a failed migration aborts the<br/>upgrade before any workload is updated"]
            pvc[("api-database-data<br/>[PersistentVolumeClaim, 2Gi]<br/>local-path locally,<br/>managed-csi on AKS")]
        end
    end

    dev -->|"aspire deploy:<br/>builds and pushes images"| registry
    registry -->|"Pulled by the cluster"| cluster
    ingress --> apiDep
    migrationJob --> pgSts
    apiDep --> pgSts
    apiDep --> cacheSts
    apiDep --> kafkaSts
    apiDep --> spatialDep
    apiDep --> geoipDep
    mcpDep --> apiDep
    consumerDep --> kafkaSts
    consumerDep --> pgSts
    tasksDep --> pgSts
    pgSts --> pvc

    classDef person fill:#08427b,stroke:#052e56,color:#ffffff
    classDef internal fill:#1168bd,stroke:#0b4884,color:#ffffff
    classDef store fill:#438dd5,stroke:#2e6295,color:#ffffff
    classDef external fill:#8b8b8b,stroke:#5f5f5f,color:#ffffff
    classDef boundary fill:none,stroke:#0b4884,stroke-dasharray:6 4,color:#0b4884
    classDef group fill:none,stroke:#9bb7d4,color:#4a6785

    class dev person
    class apiDep,spatialDep,mcpDep,consumerDep,tasksDep,migrationJob,ingress internal
    class pgSts,cacheSts,kafkaSts,pvc store
    class geoipDep,dashboardDep,registry external
    class cluster,namespace boundary
    class deployments,statefulsets group
```

`docs/DEPLOYMENT-PLAN.md` records what changes on AKS: only that Postgres, Valkey and Kafka leave
the cluster for managed services.

---

## Rendering these diagrams

### On github.com - nothing to install

GitHub renders `mermaid` fenced code blocks natively in Markdown, so this page is already a set of
diagrams in the GitHub UI - in the file view, in pull request diffs, in issues, in comments and in
the wiki. Click a rendered diagram to get GitHub's zoom, pan and copy controls, which is how you
read the container diagram on a laptop.

Two consequences worth knowing:

- A diagram change shows up as a reviewable text diff **and** GitHub renders the new version, so
  architecture drift is visible in code review.
- Mermaid's own `C4Context` / `C4Container` syntax is still experimental and lays elements out on a
  naive grid, which falls apart at this element count. These diagrams therefore use `flowchart`
  with the C4 colour and `[Type]` notation applied by convention. That is a deliberate trade:
  reliable layout, at the cost of no automatic C4 validation.

### Locally

| Tool | How |
|------|-----|
| VS Code | Install [Markdown Preview Mermaid Support](https://marketplace.visualstudio.com/items?itemName=bierner.markdown-mermaid), then <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> on this file |
| Rider / IntelliJ | Enable the Mermaid plugin (Settings, Plugins), then use the Markdown preview split view |
| Browser, no install | Paste a single block into [mermaid.live](https://mermaid.live) - useful for iterating on one diagram |
| Any editor, via CLI | `npx -p @mermaid-js/mermaid-cli mmdc -i docs/ARCHITECTURE.md -o docs/architecture.svg` - writes one numbered SVG per block |

To export a diagram for a slide deck or a wiki page, the CLI route is the one that gives you a file:

```bash
# One PNG per diagram, at 2x scale on a white background
npx -p @mermaid-js/mermaid-cli mmdc \
  -i docs/ARCHITECTURE.md \
  -o docs/architecture.png \
  --scale 2 \
  --backgroundColor white
```

### Optional: the interactive C4 model

Mermaid gives you pictures, not a model - each diagram repeats its own elements, and nothing checks
that the levels agree. If you want the real thing, `docs/architecture/workspace.dsl` describes this
architecture once in [Structurizr DSL](https://docs.structurizr.com/dsl) and derives all eight views
from it. Serve it with Docker:

```bash
docker run -it --rm -p 8080:8080 \
  -v "$(pwd)/docs/architecture:/usr/local/structurizr" \
  structurizr/structurizr local
```

On Windows PowerShell:

```powershell
docker run -it --rm -p 8080:8080 -v "${PWD}/docs/architecture:/usr/local/structurizr" structurizr/structurizr local
```

Then open <http://localhost:8080> and click into the workspace. You get navigable views (click into
a container to reach its components), a generated legend, live reload on save, and export to PNG,
SVG, PlantUML or Mermaid.

> The older `structurizr/lite` and `structurizr/cli` images are now deprecation stubs: they print a
> notice and exit 0 without doing anything, so a `validate` run against them looks like a pass.
> `structurizr/structurizr` replaces both.

Because the model is a single source, it can be checked. Both of these exit non-zero on a real
problem, so they work in a pre-commit hook or CI job:

```bash
# Syntax and broken element references
docker run --rm -v "$(pwd)/docs/architecture:/ws" \
  structurizr/structurizr validate -workspace /ws/workspace.dsl

# Model smells: elements on no view, empty or disconnected nodes, missing technology
docker run --rm -v "$(pwd)/docs/architecture:/ws" \
  structurizr/structurizr inspect -workspace /ws/workspace.dsl
```

`inspect` currently reports only advisory items - no `technology` on component-to-component calls
within a container, and no ADRs or documentation attached to the workspace. Its structural rules
are clean.

Structurizr does not render on github.com, which is why the Mermaid diagrams above exist. That does
mean two descriptions of one architecture: **treat this page as the one that must be right**, since
it is the one reviewers and newcomers see, and refresh `workspace.dsl` when you use it. The two
differ in one deliberate place - the container registry and the `aspire deploy` push appear in the
Mermaid deployment diagram but not in the Structurizr deployment view, which describes only what
runs in the cluster.
