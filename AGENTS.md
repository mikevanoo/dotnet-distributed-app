# AGENTS.md - DotNetDistributedApp

## Overview

A .NET Aspire distributed application demonstrating real-world patterns for building observable, resilient microservices. Uses weather data as the domain. The app orchestrates multiple services, databases, caches, and message brokers via Aspire's app model.

## Tech Stack

- .NET 10, C# 14, ASP.NET Core Minimal APIs
- .NET Aspire for orchestration and service defaults
- Entity Framework Core 10 with PostgreSQL (snake_case naming convention)
- Kafka with KafkaFlow for async messaging
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
- **Test (unit):** `dotnet test --project tests/DotNetDistributedApp.Api.Tests && dotnet test --project tests/DotNetDistributedApp.SpatialApi.Tests && dotnet test --project tests/DotNetDistributedApp.Events.Consumer.Tests`
- **Test (all, requires Docker):** `dotnet test`
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
  DotNetDistributedApp.ServiceDefaults/  # Aspire service defaults (telemetry, health checks)
tests/
  DotNetDistributedApp.Api.Tests/           # Unit tests for API
  DotNetDistributedApp.SpatialApi.Tests/    # Unit tests for SpatialApi
  DotNetDistributedApp.Events.Consumer.Tests/  # Unit tests for consumer
  DotNetDistributedApp.IntegrationTests/    # Aspire integration tests (requires Docker)
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

## Architecture

### How Services Connect

The `AppHost` project defines the dependency graph. When adding or modifying services, update `AppHost.cs`:

- `Api` depends on: PostgreSQL database, database migration service, SpatialApi, GeoIP container, Valkey cache, Kafka
- `Api` has `.WithReference(apiDatabaseMigrations)` and `.WaitForCompletion(apiDatabaseMigrations)` — it will not start until migrations finish
- `Events.Consumer` connects to Kafka
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
