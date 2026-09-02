/*
 * C4 model of DotNetDistributedApp, in Structurizr DSL.
 *
 * This is the optional interactive companion to docs/ARCHITECTURE.md. That page is the one that
 * renders on github.com and is the description that must be right; this file exists because a
 * single model derives every view, and the Structurizr UI navigates between the levels.
 *
 * Render (bash):
 *   docker run -it --rm -p 8080:8080 -v "$(pwd)/docs/architecture:/usr/local/structurizr" structurizr/structurizr local
 *   (PowerShell: use "${PWD}/docs/architecture" for the volume)
 * Then open http://localhost:8080 and click into the workspace.
 *
 * Check it before committing a change:
 *   docker run --rm -v "$(pwd)/docs/architecture:/ws" structurizr/structurizr validate -workspace /ws/workspace.dsl
 *   docker run --rm -v "$(pwd)/docs/architecture:/ws" structurizr/structurizr inspect  -workspace /ws/workspace.dsl
 *
 * Note: the older structurizr/lite and structurizr/cli images are now deprecation stubs that print
 * a notice and exit. structurizr/structurizr replaces both.
 *
 * The authoritative dependency graph is src/DotNetDistributedApp.AppHost/AppHost.cs. Container
 * names here match src/DotNetDistributedApp.ServiceDefaults/ResourceNames.cs.
 */
workspace "DotNetDistributedApp" "A .NET Aspire distributed application demonstrating observable, resilient microservices, using weather data as the domain." {

    !identifiers hierarchical

    configuration {
        scope softwaresystem
    }

    model {
        apiConsumer = person "API Consumer" "Explores and calls the weather and events endpoints, via the Scalar UI or any HTTP client."
        assistantUser = person "Assistant User" "Asks an LLM natural-language questions about weather stations."

        mcpHost = softwareSystem "MCP Host" "Claude Code, Claude Desktop or the MCP Inspector. Discovers and calls the weather tools on the user's behalf." {
            tags "External"
        }
        geoIp = softwareSystem "GeoIP API" "observabilitystack/geoip-api. Resolves an IP address to country, city and coordinates. A third-party image the Aspire app model hosts." {
            tags "External"
        }
        telemetry = softwareSystem "Telemetry Backend" "Receives OTLP traces, metrics and logs. The Aspire dashboard locally; any OTLP collector elsewhere." {
            tags "External"
        }

        weatherPlatform = softwareSystem "DotNetDistributedApp" "Serves weather-station data over REST and MCP, publishes and consumes domain events, and is orchestrated end to end by .NET Aspire." {

            api = container "api" "Versioned v1 and v2 weather and events endpoints. The only externally exposed container. Scalar UI at /scalar." "ASP.NET Core Minimal API" {
                weatherEndpoints = component "Weather Endpoints" "GET weather/stations and GET weather/stations/{key}/historic-data. The historic-data endpoint is output-cached for 30s, varying by stationKey." "Minimal API group"
                eventsEndpoints = component "Events Endpoints" "POST events/simple-event, events/failing-event and events/duplicate-event." "Minimal API group"
                weatherService = component "WeatherService" "Loads stations, enriches them with grid references and caller geolocation, and times the historic-data query. Returns Result<T>." "C# class"
                eventsService = component "EventsService" "Wraps the KafkaFlow producer. Lives in Api.Common and is shared with other containers." "C# class"
                coordinateConverterClient = component "CoordinateConverterClient" "Typed HttpClient. Its resilience pipeline returns 204 on failure so enrichment degrades instead of failing the request." "Typed HttpClient + Polly"
                geoIpClient = component "GeoIpClient" "Typed HttpClient. Cache-aside over HybridCache; records cache hit and miss counters." "Typed HttpClient + Polly"
                weatherDbContext = component "WeatherDbContext" "WeatherStations, WeatherStationHistoricData and ProcessedWeatherEvents. snake_case naming, retry on failure." "EF Core DbContext"
                metricsService = component "MetricsService" "Custom counters and histograms published over OpenTelemetry." "C# class"
                serviceDefaults = component "ServiceDefaults" "OTLP exporters, /health and /alive, service discovery and default resilience handlers." "Aspire service defaults"
            }

            spatialApi = container "spatial-api" "Converts WGS84 latitude/longitude to and from OSGB36 grid references. Stateless, with no dependencies of its own." "ASP.NET Core Minimal API" {
                coordinateConverterEndpoints = component "Coordinate Converter Endpoints" "GET coordinate-converter/to-os-national-grid-reference and .../to-latitude-longitude." "Minimal API group"
                coordinateConverterService = component "CoordinateConverterService" "Pure OSGB36 and WGS84 transformation. Static, no state." "C# static class"
            }

            mcpServer = container "mcp-server" "Exposes the weather domain as MCP tools. Pinned to a single replica because MCP sessions are held in process memory." "ASP.NET Core + ModelContextProtocol.AspNetCore" {
                mcpTransport = component "MCP HTTP Transport" "Streamable HTTP, stateful for clients that send initialize." "ModelContextProtocol.AspNetCore"
                weatherTools = component "WeatherTools" "list_weather_stations, get_station_historic_data and summarise_station_historic_data." "McpServerToolType"
                summariser = component "WeatherStationHistoricDataSummariser" "Aggregates a year range so the model gets a summary rather than a context-flooding row dump." "C# class"
                weatherApiClient = component "WeatherApiClient" "Typed HttpClient with Polly retry, timeout, breaker and a 204 fallback." "Typed HttpClient + Polly"
            }

            eventsConsumer = container "events-consumer" "Consumes the common topic through a retry / dead-letter / metrics / transactional-inbox pipeline. Single replica, capped by the single Kafka partition." ".NET Worker + KafkaFlow" {
                retryDeadLetterMiddleware = component "RetryDeadLetterMiddleware" "Outermost, and deliberately outside the deserializer, so a malformed payload is retried and dead lettered rather than silently dropped. Only ever sees raw bytes. Swallows the exception once a message is dead lettered." "KafkaFlow middleware"
                deserializer = component "JsonCoreDeserializer + StrictMessageTypeResolver" "Strict type resolution throws on a missing or unloadable Message-Type header, where KafkaFlow's default returns null and the message is dropped with no log." "KafkaFlow middleware"
                consumerMetricsMiddleware = component "ConsumerMetricsMiddleware" "Records events.consume_success, _failed and _unrecognised, tagged by event_name. Owns the 'not a BaseEventPayloadDto' decision for everything further in." "KafkaFlow middleware"
                deduplicationMiddleware = component "WeatherDeduplicationMiddleware" "Transactional inbox. Opens a DB transaction, short-circuits already-processed events, runs the handlers inside that transaction, records a ProcessedWeatherEvent row, and commits. Registered MiddlewareLifetime.Message." "KafkaFlow middleware"
                typedHandlerMiddleware = component "TypedHandlerMiddleware" "Dispatches by payload type. Exactly one handler per type: it runs handlers concurrently on a shared scoped DbContext." "KafkaFlow middleware"
                simpleEventHandler = component "SimpleEventMessageHandler" "Handles SimpleEventPayloadDto. Scoped, so it shares the middleware's transaction." "IMessageHandler<T>"
                failingEventHandler = component "FailingEventMessageHandler" "Always throws. Exercises retry and dead lettering. Scoped." "IMessageHandler<T>"
                dlqProducer = component "DlqProducer" "Produces the original bytes verbatim to common-dlq. Deliberately has no serializer middleware, which would base64 the bytes and overwrite the Message-Type header." "KafkaFlow producer"
            }

            scheduledTasks = container "scheduled-tasks" "Cron job that prunes aged rows from the processed-events inbox. Single replica because Coravel's PreventOverlapping mutex is in-process." ".NET Worker + Coravel" {
                cleaner = component "ProcessedWeatherEventsCleaner" "Deletes aged rows with ExecuteDeleteAsync: one delete per RetentionByEventName entry, then a catch-all on DefaultRetention. Not scoped to a consumer group." "Coravel IInvocable"
            }

            migrations = container "api-database-migrations" "Applies EF Core migrations and seed data, then exits. Every database consumer waits for it to complete, so a broken migration fails the deploy rather than the first request." ".NET Worker, runs once" {
                tags "RunOnce"
            }

            database = container "api-database" "Weather stations, historic data and the processed-events inbox, which doubles as an audit log." "PostgreSQL" {
                tags "Database"
            }

            cache = container "cache" "Output cache, HybridCache backing store and distributed cache." "Valkey" {
                tags "Database"
            }

            events = container "events" "Topics: common and common-dlq, one partition each." "Apache Kafka" {
                tags "Database"
            }
        }

        # --- Level 1 and 2 relationships -----------------------------------------------------

        apiConsumer -> weatherPlatform.api "Reads weather data and publishes events" "HTTPS/JSON"
        assistantUser -> mcpHost "Asks questions" "Natural language"
        mcpHost -> weatherPlatform.mcpServer "Lists and calls tools" "MCP over Streamable HTTP"

        weatherPlatform.mcpServer -> weatherPlatform.api "Reads weather data" "HTTP/JSON, Polly retry + timeout + breaker + fallback"
        weatherPlatform.api -> weatherPlatform.spatialApi "Converts station coordinates" "HTTP/JSON, Polly retry + timeout + breaker + fallback"
        weatherPlatform.api -> geoIp "Resolves caller geolocation, cache-aside via HybridCache" "HTTP/JSON, Polly + fallback"
        weatherPlatform.api -> weatherPlatform.database "Reads stations and historic data" "EF Core / Npgsql, retry on failure"
        weatherPlatform.api -> weatherPlatform.cache "Caches endpoint responses and GeoIP lookups" "RESP"
        weatherPlatform.api -> weatherPlatform.events "Publishes domain events to common" "Kafka, idempotent, acks=all"

        weatherPlatform.events -> weatherPlatform.eventsConsumer "Delivers events from common" "Kafka, group events-consumer"
        weatherPlatform.eventsConsumer -> weatherPlatform.events "Publishes undeliverable raw messages to common-dlq" "Kafka, no serializer"
        weatherPlatform.eventsConsumer -> weatherPlatform.database "Reads and writes the inbox inside the handler transaction" "EF Core / Npgsql"
        weatherPlatform.scheduledTasks -> weatherPlatform.database "Deletes aged inbox rows" "EF Core ExecuteDeleteAsync"
        weatherPlatform.migrations -> weatherPlatform.database "Migrates schema and seeds data" "EF Core"

        weatherPlatform.api -> telemetry "Exports traces, metrics and logs" "OTLP"
        weatherPlatform.spatialApi -> telemetry "Exports traces, metrics and logs" "OTLP"
        weatherPlatform.mcpServer -> telemetry "Exports traces, metrics and logs" "OTLP"
        weatherPlatform.eventsConsumer -> telemetry "Exports traces, metrics and logs" "OTLP"
        weatherPlatform.scheduledTasks -> telemetry "Exports traces, metrics and logs" "OTLP"

        # --- Level 3: api ---------------------------------------------------------------------

        apiConsumer -> weatherPlatform.api.weatherEndpoints "Reads weather data" "HTTPS/JSON"
        apiConsumer -> weatherPlatform.api.eventsEndpoints "Publishes events" "HTTPS/JSON"
        weatherPlatform.mcpServer.weatherApiClient -> weatherPlatform.api.weatherEndpoints "Reads weather data" "HTTP/JSON"

        weatherPlatform.api.weatherEndpoints -> weatherPlatform.api.weatherService "Calls, then maps Result<T> with ToApiResponse()"
        weatherPlatform.api.weatherEndpoints -> weatherPlatform.cache "Output caching" "RESP"
        weatherPlatform.api.eventsEndpoints -> weatherPlatform.api.eventsService "Sends events"

        weatherPlatform.api.weatherService -> weatherPlatform.api.coordinateConverterClient "Converts each station's coordinates"
        weatherPlatform.api.weatherService -> weatherPlatform.api.geoIpClient "Resolves caller geolocation"
        weatherPlatform.api.weatherService -> weatherPlatform.api.weatherDbContext "Queries stations and historic data"
        weatherPlatform.api.weatherService -> weatherPlatform.api.metricsService "Records the database query duration"
        weatherPlatform.api.geoIpClient -> weatherPlatform.api.metricsService "Records cache hits and misses"

        weatherPlatform.api.coordinateConverterClient -> weatherPlatform.spatialApi.coordinateConverterEndpoints "Converts to an OS national grid reference" "HTTP/JSON"
        weatherPlatform.api.geoIpClient -> geoIp "Looks up an IP address" "HTTP/JSON"
        weatherPlatform.api.geoIpClient -> weatherPlatform.cache "HybridCache L2" "RESP"
        weatherPlatform.api.weatherDbContext -> weatherPlatform.database "Reads and writes" "Npgsql"
        weatherPlatform.api.eventsService -> weatherPlatform.events "Produces to common" "Kafka"
        weatherPlatform.api.serviceDefaults -> telemetry "Exports traces, metrics and logs" "OTLP"

        # --- Level 3: spatial-api -------------------------------------------------------------

        weatherPlatform.spatialApi.coordinateConverterEndpoints -> weatherPlatform.spatialApi.coordinateConverterService "Transforms coordinates"

        # --- Level 3: mcp-server --------------------------------------------------------------

        mcpHost -> weatherPlatform.mcpServer.mcpTransport "Lists and calls tools" "MCP over Streamable HTTP"
        weatherPlatform.mcpServer.mcpTransport -> weatherPlatform.mcpServer.weatherTools "Invokes a tool"
        weatherPlatform.mcpServer.weatherTools -> weatherPlatform.mcpServer.summariser "Aggregates a year range"
        weatherPlatform.mcpServer.weatherTools -> weatherPlatform.mcpServer.weatherApiClient "Fetches stations and historic data"

        # --- Level 3: events-consumer ---------------------------------------------------------

        weatherPlatform.events -> weatherPlatform.eventsConsumer.retryDeadLetterMiddleware "Delivers raw messages from common" "Kafka, 3 workers"
        weatherPlatform.eventsConsumer.retryDeadLetterMiddleware -> weatherPlatform.eventsConsumer.deserializer "Invokes next"
        weatherPlatform.eventsConsumer.deserializer -> weatherPlatform.eventsConsumer.consumerMetricsMiddleware "Invokes next with the deserialized payload"
        weatherPlatform.eventsConsumer.consumerMetricsMiddleware -> weatherPlatform.eventsConsumer.deduplicationMiddleware "Invokes next for a recognised payload"
        weatherPlatform.eventsConsumer.deduplicationMiddleware -> weatherPlatform.eventsConsumer.typedHandlerMiddleware "Invokes next inside the transaction"
        weatherPlatform.eventsConsumer.typedHandlerMiddleware -> weatherPlatform.eventsConsumer.simpleEventHandler "Dispatches SimpleEventPayloadDto"
        weatherPlatform.eventsConsumer.typedHandlerMiddleware -> weatherPlatform.eventsConsumer.failingEventHandler "Dispatches FailingEventPayloadDto"
        weatherPlatform.eventsConsumer.retryDeadLetterMiddleware -> weatherPlatform.eventsConsumer.dlqProducer "Dead letters after retries are exhausted"
        weatherPlatform.eventsConsumer.dlqProducer -> weatherPlatform.events "Produces raw bytes to common-dlq" "Kafka"
        weatherPlatform.eventsConsumer.deduplicationMiddleware -> weatherPlatform.database "Reads and writes ProcessedWeatherEvents in one transaction" "EF Core"

        # --- Level 3: scheduled-tasks ---------------------------------------------------------

        weatherPlatform.scheduledTasks.cleaner -> weatherPlatform.database "Deletes aged inbox rows" "EF Core ExecuteDeleteAsync"

        # --- Deployment: local Kubernetes (Rancher Desktop k3s) -------------------------------

        deploymentEnvironment "Kubernetes" {
            k8s = deploymentNode "Kubernetes cluster" "Rancher Desktop k3s locally; AKS in the cloud." "Kubernetes" {
                ingressNode = infrastructureNode "ingress" "Ingress class traefik, with api as the default backend. TLS terminates here, which is why the chart publishes only http endpoints for the services behind it." "Kubernetes Ingress"

                ns = deploymentNode "namespace: dotnet-distributed-app" "Helm release dotnet-distributed-app, chart version 0.1.0." "Kubernetes namespace" {

                    apiDeployment = deploymentNode "api-deployment" "Stateless. Scale with replicas or an HPA; the Service load-balances to whatever is Ready." "Kubernetes Deployment" {
                        apiInstance = containerInstance weatherPlatform.api
                    }
                    deploymentNode "spatial-api-deployment" "Stateless. Scales the same way as api." "Kubernetes Deployment" {
                        containerInstance weatherPlatform.spatialApi
                    }
                    deploymentNode "mcp-server-deployment" "PinToSingleReplica: MCP sessions are held in process memory." "Kubernetes Deployment, 1 replica" {
                        containerInstance weatherPlatform.mcpServer
                    }
                    deploymentNode "events-consumer-deployment" "PinToSingleReplica: throughput is capped by the single Kafka partition." "Kubernetes Deployment, 1 replica" {
                        containerInstance weatherPlatform.eventsConsumer
                    }
                    deploymentNode "scheduled-tasks-deployment" "PinToSingleReplica: Coravel's PreventOverlapping mutex is in-process, so two replicas would both fire." "Kubernetes Deployment, 1 replica" {
                        containerInstance weatherPlatform.scheduledTasks
                    }
                    deploymentNode "api-database-migrations-job" "PublishAsKubernetesJob, wired as a Helm pre-upgrade hook. A failed migration aborts the upgrade before any workload is updated." "Kubernetes Job" {
                        containerInstance weatherPlatform.migrations
                    }
                    databaseStatefulSet = deploymentNode "api-database-server-statefulset" "Managed Postgres on AKS - see docs/DEPLOYMENT-PLAN.md." "Kubernetes StatefulSet" {
                        databaseInstance = containerInstance weatherPlatform.database
                        databaseVolume = infrastructureNode "api-database-data" "2Gi. local-path on k3s, managed-csi on AKS. Without it the database lands on an emptyDir and every pod restart wipes it." "PersistentVolumeClaim" {
                            tags "Database"
                        }
                    }
                    deploymentNode "cache-statefulset" "Managed Valkey/Redis on AKS." "Kubernetes StatefulSet" {
                        containerInstance weatherPlatform.cache
                    }
                    deploymentNode "events-statefulset" "Managed Kafka/Event Hubs on AKS." "Kubernetes StatefulSet" {
                        containerInstance weatherPlatform.events
                    }
                    deploymentNode "geoip-api-deployment" "Third-party image." "Kubernetes Deployment" {
                        tags "External"
                        softwareSystemInstance geoIp
                    }
                    deploymentNode "k8s-dashboard-deployment" "Aspire dashboard, deployed into the cluster as the OTLP sink." "Kubernetes Deployment" {
                        tags "External"
                        softwareSystemInstance telemetry
                    }
                }
            }

            k8s.ingressNode -> k8s.ns.apiDeployment.apiInstance "Routes external traffic to" "HTTPS"
            k8s.ns.databaseStatefulSet.databaseInstance -> k8s.ns.databaseStatefulSet.databaseVolume "Persists data to" "Filesystem"
        }
    }

    views {
        systemContext weatherPlatform "SystemContext" {
            include *
            include assistantUser
            autolayout tb
            description "Level 1. Who uses the system and what it depends on."
        }

        container weatherPlatform "Containers" {
            include *
            autolayout tb
            description "Level 2. The deployable pieces and how they talk. Mirrors AppHost.cs."
        }

        component weatherPlatform.api "Components-Api" {
            include *
            autolayout tb
            description "Level 3. Endpoints call services directly - no repository, mapper or mediator."
        }

        component weatherPlatform.spatialApi "Components-SpatialApi" {
            include *
            autolayout tb
            description "Level 3. A pure coordinate transformation behind two endpoints."
        }

        component weatherPlatform.mcpServer "Components-McpServer" {
            include *
            autolayout tb
            description "Level 3. A thin adapter over the public REST API - it owns no data."
        }

        component weatherPlatform.eventsConsumer "Components-EventsConsumer" {
            include *
            autolayout tb
            description "Level 3. The middleware pipeline, outside-in. Ordering is the architecture: see AGENTS.md."
        }

        component weatherPlatform.scheduledTasks "Components-ScheduledTasks" {
            include *
            autolayout tb
            description "Level 3. One Coravel invocable that prunes the inbox."
        }

        deployment weatherPlatform "Kubernetes" "Deployment-Kubernetes" {
            include *
            autolayout tb
            description "What the generated Helm chart installs. Kinds match the rendered chart."
        }

        styles {
            element "Person" {
                shape person
                background #08427b
                color #ffffff
            }
            element "Software System" {
                background #1168bd
                color #ffffff
            }
            element "Container" {
                background #438dd5
                color #ffffff
            }
            element "Component" {
                background #85bbf0
                color #000000
            }
            element "Database" {
                shape cylinder
            }
            element "RunOnce" {
                shape roundedbox
                background #7aa8d2
            }
            element "External" {
                background #8b8b8b
                color #ffffff
            }
            element "Deployment Node" {
                background #ffffff
                color #444444
                stroke #888888
            }
            relationship "Relationship" {
                thickness 2
            }
        }
    }
}
