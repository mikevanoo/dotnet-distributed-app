# AGENTS.md - DotNetDistributedApp

## Overview

A .NET Aspire distributed application demonstrating real-world patterns for building observable, resilient microservices. Uses weather data as the domain. The app orchestrates multiple services, databases, caches, and message brokers via Aspire's app model.

## Tech Stack

- .NET 10, C# 14, ASP.NET Core Minimal APIs
- .NET Aspire for orchestration and service defaults
- Entity Framework Core 10 with PostgreSQL (snake_case naming convention)
- Kafka with KafkaFlow for async messaging
- Coravel for scheduled background jobs
- Valkey (Redis-compatible) for output caching, HybridCache backing store, and distributed caching
- Scalar.AspNetCore for API documentation UI (OpenAPI)
- Microsoft.Extensions.Http.Resilience for resilient HTTP client policies
- Serilog for structured logging
- OpenTelemetry for metrics and tracing
- FluentResults for error handling (`Result<T>` pattern)
- CSharpier for code formatting
- xUnit v3 with Microsoft.Testing.Platform, NSubstitute, AwesomeAssertions

## Prerequisites

- **.NET SDK** - version pinned in `global.json`
- **Docker** - required for integration tests and `dotnet run` (Aspire spins up containers for Postgres, Kafka, Valkey, GeoIP, etc.)
- **PowerShell** (optional) - needed only for `.ps1` scripts; bash equivalents exist for lint commands

## Commands

Run these from the repository root.

- **Build:** `dotnet build`
- **Run:** `dotnet run --project src/DotNetDistributedApp.AppHost` (starts all services via Aspire)
- **Test (unit):** `dotnet test --project tests/DotNetDistributedApp.Api.Tests && dotnet test --project tests/DotNetDistributedApp.SpatialApi.Tests && dotnet test --project tests/DotNetDistributedApp.Events.Consumer.Tests && dotnet test --project tests/DotNetDistributedApp.McpServer.Tests`
- **Test (all, requires Docker):** `dotnet test`
- **Test (deployed Kubernetes release):** `pwsh ./deployment-test.ps1` or `./deployment-test.sh` - opt-in, needs a deployed cluster; `-ChartOnly` / `--chart-only` asserts on the rendered Helm chart and needs no cluster
- **Test (code coverage):** `./coverage-report.ps1`
- **Lint check:** `pwsh ./lint-check.ps1` or `./lint-check.sh` (runs `dotnet format analyzers --verify-no-changes` and `dotnet csharpier check .`)
- **Lint fix:** `pwsh ./lint-fix.ps1` or `./lint-fix.sh` (runs `dotnet format analyzers` and `dotnet csharpier format .`)
- **Restore tools:** `dotnet tool restore` (installs tools defined in `.config/dotnet-tools.json`)
- **Add EF Core migration:** `dotnet ef migrations add <MigrationName> --project src/DotNetDistributedApp.Api.Data --startup-project src/DotNetDistributedApp.Api`

Run `dotnet tool restore` first if tools have not been restored (required for lint and coverage commands).

Always run `./lint-fix.sh` (or `pwsh ./lint-fix.ps1` on Windows) before committing. The CI pipeline enforces both analyzer rules and CSharpier formatting.

## Project Structure

```
src/
  DotNetDistributedApp.AppHost/       # Aspire orchestrator - defines all resources and dependencies
  DotNetDistributedApp.Api/           # Main REST API (weather endpoints, event publishing)
  DotNetDistributedApp.Api.Common/    # Shared code: events, metrics, error types, FluentResults extensions
  DotNetDistributedApp.Api.Data/      # EF Core DbContext, entities, migrations, seed data
  DotNetDistributedApp.Api.Data.MigrationService/  # Worker service that runs EF Core migrations
  DotNetDistributedApp.SpatialApi/    # Upstream microservice for coordinate conversion
  DotNetDistributedApp.Events.Consumer/  # Kafka consumer service
  DotNetDistributedApp.ScheduledTasks/   # Coravel scheduled jobs (purges the processed events inbox)
  DotNetDistributedApp.McpServer/         # MCP server exposing the weather API as tools
  DotNetDistributedApp.ServiceDefaults/  # Aspire service defaults (telemetry, health checks)
tests/
  DotNetDistributedApp.Api.Tests/           # Unit tests for API
  DotNetDistributedApp.SpatialApi.Tests/    # Unit tests for SpatialApi
  DotNetDistributedApp.Events.Consumer.Tests/  # Unit tests for consumer
  DotNetDistributedApp.McpServer.Tests/     # Unit tests for MCP server tools
  DotNetDistributedApp.IntegrationTests/    # Aspire integration tests (requires Docker)
  DotNetDistributedApp.DeploymentTests/     # Tests against a deployed Kubernetes release (opt-in)
```

### Key Files

- `Directory.Build.props` - shared MSBuild properties (target framework, nullable, implicit usings)
- `Directory.Packages.props` - central package version management
- `.editorconfig` - comprehensive C# style, naming, and formatting rules
- `global.json` - pinned .NET SDK version (do not change without explicit request)
- `DotNetDistributedApp.slnx` - solution file
- `.config/dotnet-tools.json` - defines required .NET tools (CSharpier, NSwag, dotnet-coverage, reportgenerator). If tools aren't restored, lint and coverage commands fail.
- `coverage.runsettings` - controls code coverage exclusions, referenced by the coverage command
- `src/DotNetDistributedApp.ServiceDefaults/ResourceNames.cs` - shared constants for Aspire resource names used throughout AppHost
- `docs/K8S-DEPLOYMENT-TESTING-COMMANDS.md` - how to deploy to a local cluster and verify it by hand. Most of its checks are automated in `tests/DotNetDistributedApp.DeploymentTests`; the page says which test class covers each section and keeps the parts that cannot be automated.
- `docs/ARCHITECTURE.md` - the C4 model, levels 1-3 plus a deployment view, as Mermaid so it renders on github.com. `docs/architecture/workspace.dsl` is the same architecture as a Structurizr model for interactive local browsing. Both are a reading of `AppHost.cs`, which stays authoritative: if you change the service dependency graph, update the container diagram and the DSL.
- `docs/KAFKA-IDEMPOTENCY-PLAN.md` - the design record for the consumer's transactional inbox: the options considered, why the DB-backed one was chosen, and the KafkaFlow/EF Core behaviour each decision rests on. The constraints it produced are summarised in the sections above; read it when you need the reasoning rather than the rule.

## Architecture

### How Services Connect

The `AppHost` project defines the dependency graph. When adding or modifying services, update `AppHost.cs`:

- `Api` depends on: PostgreSQL database, database migration service, SpatialApi, GeoIP container, Valkey cache, Kafka
- `Api` has `.WithReference(apiDatabaseMigrations)` and `.WaitForCompletion(apiDatabaseMigrations)` — it will not start until migrations finish
- `Events.Consumer` connects to Kafka
- `ScheduledTasks` connects to the PostgreSQL database and has `.WaitForCompletion(apiDatabaseMigrations)`
- `SpatialApi` is standalone (no external dependencies)

**Note:** `src/DotNetDistributedApp.AppHost/ValkeyBuilderExtensions.cs` is a custom extension adapted from Aspire source code that provides `WithRedisInsightForValkey()`. This is not a standard Aspire method — do not search for it in Aspire docs.

### Patterns Used

- **Minimal API with extension methods** - endpoints are registered via `Map*Endpoints()` extension methods on `WebApplication`, service registration via `Add*Services()` extension methods on `WebApplicationBuilder`. Each feature area (Weather, Events) has its own extension methods file.
- **Request parameter/model validation** - use `System.ComponentModel.DataAnnotations` attributes. 
- **FluentResults** - all service methods return `Result<T>`. Convert to HTTP responses using `.ToApiResponse()` extension method, which maps `NotFoundError` to 404, other failures to ProblemDetails.
- **Primary constructors for DI** - services use primary constructors (e.g., `public class WeatherService(WeatherDbContext dbContext, ...)`).
- **Serilog source-generated logging** - use `[LoggerMessage]` attribute with `partial` methods for high-performance structured logging. Do not use `Console.WriteLine` or string interpolation in log calls.
- **Central package management** - all NuGet package versions are in `Directory.Packages.props`. Individual `.csproj` files reference packages without versions.
- **Scalar for OpenAPI documentation** - both Api and SpatialApi expose interactive API documentation via `app.MapScalarApiReference()`. New endpoints are automatically documented.
- **DTO generation from OpenAPI spec** - use `NSwag` to generate DTOs (not full client classes) from OpenAPI spec. See `src/DotNetDistributedApp.Api/Clients/generate-dtos.ps1`.

### Kafka Consumer Idempotency (Constraints)

The consumer pipeline is a transactional inbox: `WeatherDeduplicationMiddleware` opens a DB transaction, skips already-processed events, runs the handlers inside that transaction, and records a `ProcessedWeatherEvent` row. Handler DB writes therefore commit atomically with the "event processed" record. Registration lives in `src/DotNetDistributedApp.Events.Consumer/ServiceCollectionExtensions.cs`.

Three non-obvious constraints hold this together. All three are enforced by `EventsConsumerRegistrationShould` - if you break one, that test tells you why.

- **Exactly one `IMessageHandler<T>` per payload type.** KafkaFlow's `TypedHandlerMiddleware` runs all handlers for a payload type **concurrently** (`Task.WhenAll`). Since handlers are scoped, two handlers for one payload type would share a single `WeatherDbContext` in parallel, which is not thread-safe. To react to one event in several ways, do it in one handler.
- **Every `AddTypedHandlers` call needs `WithHandlerLifetime(InstanceLifetime.Scoped)`.** KafkaFlow defaults handlers to `Singleton`, and the setting does not carry across `AddTypedHandlers` calls. A singleton handler resolves a captive root `WeatherDbContext` instead of the per-message scoped one, so its writes fall outside the middleware's transaction.
- **Middleware that injects a `DbContext` must be registered `MiddlewareLifetime.Message`.** `Add<T>()` defaults to `ConsumerOrProducer`, which shares one instance across all workers of a consumer.

### Kafka Consumer Data Loss (Constraints)

KafkaFlow's `ConsumerWorker.ProcessMessageAsync` catches **every** non-cancellation pipeline exception, logs it, and its `finally` still calls `ConsumerContext.Complete()` — which stores the offset. So an unhandled failure is *silently dropped, not retried*, and **rethrowing does not protect a message**. Three registrations in `AddEventsConsumerKafka` exist solely because of that, and all three are guarded by `EventsConsumerRegistrationShould` or `UnreadableMessageDeadLetteringShould`.

- **`RetryDeadLetterMiddleware` is registered first, outside `AddDeserializer`.** `DeserializerConsumerMiddleware` has no `try`/`catch`, so with the deserializer outermost a malformed payload throws past retry/DLQ and is lost. Consequence of the order: this middleware only ever sees raw bytes, never a deserialized payload — the deserializer passes the deserialized value inward on a *new* `IMessageContext`.
- **The DLQ producer has no serializer middleware.** It is handed the original bytes and produces them verbatim. Adding `AddSerializer<JsonCoreSerializer>()` would base64 the bytes into a JSON string and overwrite the `Message-Type` header with `System.Byte[]`, making every dead letter unreadable.
- **`AddDeserializer` uses `StrictMessageTypeResolver`.** KafkaFlow's `DefaultTypeResolver` returns `null` for a missing or unloadable `Message-Type` header, and the deserializer answers `null` with a bare `return` — no handler, no exception, no log, no `MessageConsumeError` event, offset stored regardless. Strict resolution throws instead so the message is dead lettered. Note the trade-off: a payload type this process cannot load is now a dead letter, not an ignored message. A payload type with *no handler* is still ignored harmlessly.
- **When a DLQ produce fails, set `context.ConsumerContext.ShouldStoreOffset = false`.** That is the only thing that keeps the offset uncommitted. It costs durable progress on that partition — KafkaFlow's `OffsetManager` commits contiguously, so a context that is never marked processed pins the committed offset and later offsets accumulate in memory until a restart replays them (the inbox absorbs the replay). Correct over losing the message, but it makes a failing DLQ produce an incident.

Do **not** set `EnableAutoCommit`, `EnableAutoOffsetStore` or `AutoCommitIntervalMs` on `ConsumerConfig`: `ConsumerConfigurationBuilder.Build` overwrites all three. KafkaFlow owns committing, and `WithAutoCommitIntervalMs` (default 5s) is the only knob that moves the window between storing an offset and committing it — the window that makes redelivery of already-processed messages possible, and the reason the inbox exists.

The inbox table doubles as an audit log, so it needs pruning. `ProcessedWeatherEventsCleaner` in `src/DotNetDistributedApp.ScheduledTasks` is a Coravel `IInvocable` that deletes aged rows with `ExecuteDeleteAsync`: one delete per entry in the `ProcessedWeatherEventsCleaner:RetentionByEventName` config section, then a catch-all for every other event name using `DefaultRetention`. Two things to know before changing it:

- **Retention keys are the `EventName` values from the payload DTOs** (e.g. `simple-event`, `failing-event`), matched case-sensitively in SQL. A key that does not match an `EventName` silently falls into the `DefaultRetention` catch-all rather than failing.
- **The cleaner is not scoped to a consumer group.** It deletes across every group, so the `scheduled-tasks` service running during an integration test run is deleting from the same table the tests assert on. Keep retention windows in a test far longer than the rows it seeds.

### Kafka Consumer Metrics (Constraints)

`ConsumerMetricsMiddleware` records `events.consume_success`, `events.consume_failed` and `events.consume_unrecognised`. It is registered between `AddDeserializer` and `WeatherDeduplicationMiddleware` because that is the only position satisfying all three constraints below, and both halves of it are guarded by `EventsConsumerRegistrationShould`.

- **Inside the deserializer.** All three counters are tagged `event_name`, which only exists on a deserialized payload. Registered outside it, `context.Message.Value` is raw bytes before *and* after `next` returns.
- **Inside `RetryDeadLetterMiddleware`.** That middleware swallows the exception once it has successfully dead lettered a message, so a counter outside it records a poison message as a success.
- **Outside `WeatherDeduplicationMiddleware`.** This middleware owns the "not a `BaseEventPayloadDto`" decision and short-circuits those messages, which is what lets the deduplication middleware *cast* `context.Message.Value` rather than re-check it. Swap the two and every unrecognised message becomes an `InvalidCastException` that burns the retry backoff before being dead lettered.

`events.consume_failed` fires once per **attempt** (matching `LogMessageHandlingFailed`) and is the main *metric* signal that a handler failed, since the worker swallows the exception. It cannot see a deserialization failure — that happens outside it — but those are dead lettered rather than dropped, so the DLQ is the signal for them. A duplicate skipped by the inbox counts as a **success**: it was consumed without error, and `events.consume_duplicate` is the orthogonal dimension.

### Patterns NOT Used (Never Suggest)

- Repository pattern - use EF Core `DbContext` directly
- AutoMapper or Mapperly - write explicit mappings
- MediatR/Mediator - this project calls services directly from endpoints
- Exceptions for control flow - use `FluentResults` `Result<T>` instead
- `Console.WriteLine` - use Serilog
- Block-scoped namespaces - always use file-scoped namespaces

## Code Conventions

### Style and Formatting

- CSharpier handles all formatting. Do not manually adjust whitespace, line breaks, or indentation.
- The `.editorconfig` defines the full set of analyzer rules and naming conventions. Key rules:
  - File-scoped namespaces (enforced as error)
  - `var` everywhere
  - Expression-bodied members preferred
  - Allman-style braces (opening brace on new line)
  - Private fields: `_camelCase`
  - All public members: `PascalCase`
  - Interfaces: `I` prefix
  - Always pass `CancellationToken` to async methods
  - Use `is null` / `is not null` instead of `== null` / `!= null`
  - Use pattern matching and switch expressions where possible
  - Use `nameof` instead of string literals for member names

### Naming Conventions

- Services: `[Feature]Service` (e.g., `WeatherService`, `EventsService`)
- HTTP clients: `[ExternalService]Client` (e.g., `GeoIpClient`, `CoordinateConverterClient`)
- DTOs: `[Name]Dto` (e.g., `WeatherStationDto`, `GeoIpResponseDto`)
- Extension method classes: `[Purpose]Extensions` or `[Purpose]WebApplicationExtensions`
- Kafka message handlers: `[EventName]MessageHandler` (e.g., `SimpleEventMessageHandler`)
- Constants: nested static classes (e.g., `Constants.CachePolicy.WeatherStationHistoricData`)

### Code Examples

Endpoint registration pattern:

```csharp
public static WebApplication MapWeatherEndpoints(this WebApplication webApplication)
{
    var weatherGroup = webApplication.MapGroup("/weather");
    weatherGroup.MapGet(
        "/stations",
        async ([FromServices] WeatherService weatherService, CancellationToken cancellationToken) =>
            (await weatherService.GetWeatherStations(cancellationToken)).ToApiResponse()
    );
    return webApplication;
}
```

Service method returning Result:

```csharp
public async Task<Result<ResponseDto<List<WeatherStationDto>>>> GetWeatherStations(
    CancellationToken cancellationToken = default)
{
    var stations = await dbContext.WeatherStations.OrderBy(x => x.DisplayName).ToListAsync(cancellationToken);
    // ... transform and enrich ...
    return Result.Ok(ResponseDto.Create(result, geoInfo));
}
```

Source-generated logging:

```csharp
public partial class SimpleEventMessageHandler(ILogger<SimpleEventMessageHandler> logger)
    : IMessageHandler<SimpleEventPayloadDto>
{
    [LoggerMessage(LogLevel.Information, "Handling simple event: {Value}")]
    private partial void LogHandlingSimpleEvent(string value);
}
```

## Testing

### Conventions

- Whenever possible, follow test-driven development (TDD) principles: red/green/refactor.
- Test classes: `[ClassUnderTest]Should` (e.g., `CoordinateConverterClientShould`, `WeatherStationsShould`)
- Test methods: descriptive sentences without underscores (e.g., `FormatCoordinatesWithInvariantCultureInUrl`, `GetWeatherStationsReturn200OkAndExpectedNumberOfStations`)
- Do not emit "Arrange", "Act", or "Assert" comments - separate test setup, execution, and assertion phases with a single blank line.
- Use AwesomeAssertions (FluentAssertions fork) for all assertions (e.g., `.Should().BeTrue()`, `.Should().Be200Ok()`)
- Use NSubstitute for mocking (e.g., `Substitute.For<ILogger<T>>()`)
- Use xUnit v3 with `[Fact]` and `[Theory]` attributes
- Use `TestContext.Current.CancellationToken` in unit tests for cancellation tokens
- Copy the style of nearby test files when adding new tests

### Integration Tests

Integration tests use `Aspire.Hosting.Testing` to spin up the full `AppHost` with real infrastructure (Docker containers for Postgres, Kafka, Valkey, etc.).

- They require Docker to be running
- They use `AppHostFixture` as a shared assembly-level fixture
- They are slow (~60s startup) - do not run them casually
- Run unit tests first (see above)

## Boundaries

### Always Do

- Run `./lint-fix.sh` (or `pwsh ./lint-fix.ps1` on Windows) before committing
- Add new package versions to `Directory.Packages.props`, not to individual `.csproj` files
- Follow existing patterns when adding new endpoints, services, or message handlers
- Add or update tests for code you change
- Use `CancellationToken` in all async methods

### Ask First

- Before modifying the `AppHost` service dependency graph
- Before adding new NuGet packages
- Before changing database schema or adding migrations
- Before modifying CI pipeline (`.github/workflows/`)

### Never Do

- Modify EF Core migration files that have already been applied
- Put secrets, API keys, or connection strings in code or config committed to git
- Change `global.json` without explicit request
- Change `Directory.Build.props` or `Directory.Packages.props` without understanding the impact
- Add `Console.WriteLine` or use string interpolation in log calls
- Use block-scoped namespaces
- Run integration tests as part of quick feedback loops (they require Docker and are slow)
- Add the deployment tests to CI, or make them run without their `RUN_DEPLOYMENT_TESTS` / `RUN_CHART_TESTS` opt-in - they need a deployed cluster and they write to it
- Register a second `IMessageHandler<T>` for a payload type that already has one, or register message handlers without `WithHandlerLifetime(InstanceLifetime.Scoped)` - see [Kafka Consumer Idempotency (Constraints)](#kafka-consumer-idempotency-constraints)
- Reorder the consumer middlewares so the deserializer wraps `RetryDeadLetterMiddleware`, add a serializer to the DLQ producer, or swap `StrictMessageTypeResolver` back to KafkaFlow's default - each one silently loses messages, see [Kafka Consumer Data Loss (Constraints)](#kafka-consumer-data-loss-constraints)
- Move `ConsumerMetricsMiddleware` outside the deserializer or inside `WeatherDeduplicationMiddleware` - the first makes its counters untaggable, the second breaks a cast, see [Kafka Consumer Metrics (Constraints)](#kafka-consumer-metrics-constraints)
