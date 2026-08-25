# Idempotency for the Kafka Event Consumer — Transactional Inbox (PostgreSQL)

## Context

`DotNetDistributedApp.Events.Consumer` consumes the `common` topic with KafkaFlow and has no
deduplication. A message **can be processed more than once**, so we want the consumer to be
**idempotent** — process the same message twice, produce the effect once.

**Where the duplicates actually come from** (verified against KafkaFlow 4.2.0 source —
`ConsumerWorker.ProcessMessageAsync`, and `ConsumerConfigurationBuilder` where
`_autoMessageCompletion = true` by default and this project never calls
`WithManualMessageCompletion()`):

1. **Offset storage is asynchronous from the commit.** On success the worker's `finally` calls
   `Complete()`, which *stores* the offset; librdkafka *commits* it later on
   `auto.commit.interval.ms` (~5s default). A crash or rebalance inside that window redelivers
   messages that were already fully processed. **This is the primary duplicate source.**
2. **Cancellation mid-message.** An `OperationCanceledException` under cancellation is the *only*
   path that sets `ShouldStoreOffset = false`, so that message is redelivered on restart — including
   after its side effects partially applied.
3. **In-process retries.** `RetryDeadLetterMiddleware` re-invokes the handler up to `MaxRetryCount`
   times, so effects applied before a mid-handler failure are re-applied on the next attempt.

Note what is *not* a duplicate source: the worker catches **every** pipeline exception, logs it, and
still completes the message. Unhandled failures are therefore **silently dropped, not retried** — see
the pre-existing findings section at the end, which are out of scope here but worth tracking.

Two facts from exploration shape the design:

1. **No stable message identifier exists.** `BaseEventPayloadDto`
   (`src/DotNetDistributedApp.Api.Common/Events/BaseEventPayloadDto.cs`) exposes only `EventName`
   and a mutable `PartitionKey` (a per-publish `Guid.NewGuid()` used as the Kafka key — semantics
   are "partitioning", not "identity"). **Decision (confirmed): add a producer-generated
   `EventId` (Guid) to the payload** as the idempotency key.
2. **The consumer has no storage wired.** It references only Kafka/KafkaFlow, `Api.Common`, and
   `ServiceDefaults`. Any dedup store is a net-new dependency.

**Chosen strategy: Option 2 — a transactional inbox table in PostgreSQL.** Rationale (per the
requester): when the handlers gain real side effects, those will be **database writes the vast
majority of the time**, so a DB-backed inbox lets the "record processed" row commit in the *same
transaction* as the business write — exactly-once *effect*, not just best-effort dedup. The inbox
table doubles as an **audit log** of every processed event. It needs a **configurable cleanup job**
that purges rows with **per-event-type retention windows**.

> The other two strategies considered were (1) a Valkey/Redis dedup middleware — lighter but not
> transactional with DB side effects and non-durable (Valkey has no data volume) — and (3) natural
> idempotency + idempotent producer — no store, but not a general guarantee. Option 3's one-line
> producer-idempotence tweak is still worth adopting alongside (see step 8).

---

## Design overview

- **One database, one business context.** The app has a single Postgres database (`api-database`)
  and a single business `DbContext` (`WeatherDbContext` in `Api.Data`) migrated by the existing
  `Api.Data.MigrationService` (`Worker.cs:20`). We add a `ProcessedEvent` inbox entity **to
  `WeatherDbContext`**. Consequences:
  - The **existing migration pipeline applies the new table with zero new wiring** — a normal
    `dotnet ef migrations add` produces a `WeatherDbContext` migration and the current
    MigrationService `Worker` applies it.
  - Future handler DB writes (via the same scoped `WeatherDbContext`) commit **atomically** with
    the inbox row, giving true exactly-once effect for free.
  - *Trade-off:* the consumer takes a project reference on `Api.Data`, coupling it to the weather
    context. Acceptable given the single-database design; noted as the alternative below if
    decoupling is later wanted.
- **Where dedup happens:** a new `InboxDeduplicationMiddleware` (`IMessageMiddleware`) placed
  **after** `RetryDeadLetterMiddleware` and **before** the typed handlers. It opens a transaction,
  short-circuits duplicates, runs the handler inside the transaction, records the `ProcessedEvent`
  row, and commits. A handler failure rolls the transaction back (no row written) and propagates so
  `RetryDeadLetterMiddleware` retries / DLQs — a DLQ'd message is therefore never marked processed.
- **Transaction sharing — VERIFIED against KafkaFlow 4.2.0 source + a DI probe.** The middleware and
  the handlers *do* resolve the same scoped `WeatherDbContext`, **but only once the handlers are
  registered `Scoped`, which they are not by default.** The full chain:
  1. `ConsumerWorkerPool.CreateMessageContext` (`:176`) calls `_consumerDependencyResolver.CreateScope()`
     **once per message** and passes `messageDependencyScope.Resolver` as `MessageContext.DependencyResolver`.
     The scope is disposed in `ConsumerContext.Complete()` (`:75`) — i.e. *after* the pipeline returns.
  2. `MessageContext.SetMessage` copies `DependencyResolver` into the new context (`MessageContext.cs:53`),
     so the deserializer creating a new context does **not** break the chain.
  3. `MiddlewareLifetime.Message` maps to `InstanceLifetime.Scoped`
     (`MiddlewareConfigurationBuilder.ParseLifetime:53`) and is resolved from `context.DependencyResolver`
     (`MiddlewareExecutor.cs:85`) → our middleware's `WeatherDbContext` comes from the message scope.
  4. `AddTypedHandlers` registers `TypedHandlerMiddleware` at `MiddlewareLifetime.Message` too
     (`ConfigurationBuilderExtensions.cs:196-198`), via the factory overload whose `IDependencyResolver`
     wraps the *scoped* provider (`MicrosoftDependencyConfigurator`: `provider => factory(new MicrosoftDependencyResolver(provider))`).
     `TypedHandlerMiddleware.Invoke` then resolves each handler from it (`:39`).
  5. **The gap:** `TypedHandlerConfigurationBuilder._serviceLifetime` defaults to
     `InstanceLifetime.Singleton` (`:19`) and `Program.cs` never calls `WithHandlerLifetime`. A singleton
     resolved through a scope is still the root instance, so a handler injecting `WeatherDbContext` gets
     a *different* context — see the required fix in step 4.

  Probe results (`ServiceCollection` built from the real consumer registration): `handler=Singleton`,
  `middleware=Scoped`, `dbContext=Scoped`; handler instance shared across two scopes, `DbContext` not.

> Decoupled alternative (not chosen): a separate `EventInboxDbContext` in a new
> `DotNetDistributedApp.Events.Data` project against the same database, with its own
> `__efmigrationshistory_events` table and the MigrationService extended to migrate it. Cleaner
> separation, but loses free cross-context atomicity and adds migration wiring.

---

## Implementation plan

### 1. Add the idempotency key
`src/DotNetDistributedApp.Api.Common/Events/BaseEventPayloadDto.cs` — add
`public Guid EventId { get; set; } = Guid.CreateVersion7();` (settable so System.Text.Json rehydrates
it on the consumer; defaulted so the producer always populates it). No producer call-site change is
required. The consumer reads it via `context.Message.Value as BaseEventPayloadDto` (as
`RetryDeadLetterMiddleware.cs:57` already casts the value).

`Guid.CreateVersion7()` (not `Guid.NewGuid()`) gives a **time-ordered** UUID, which keeps inbox
inserts append-mostly in the primary-key B-tree — see the key performance notes in step 2.

### 2. Add the `ProcessedEvent` inbox entity + mapping
- New file `src/DotNetDistributedApp.Api.Data/Inbox/ProcessedEvent.cs`:
  - `EventId` (Guid) + `ConsumerGroup` (string) — **composite primary key, in that order** (the dedup
    key). Ordering rationale below.
  - `EventName` (string) — indexed; drives per-type cleanup.
  - `Topic`, `PartitionKey` (strings) — audit context.
  - `Partition` (int), `Offset` (long) — from `context.ConsumerContext`, audit.
  - `ProcessedAtUtc` (DateTimeOffset → `timestamptz`) — audit + cleanup cutoff.
- `src/DotNetDistributedApp.Api.Data/Weather/WeatherDbContext.cs` — add
  `public DbSet<ProcessedEvent> ProcessedEvents { get; set; }` and a private
  `CreateProcessedEvents(modelBuilder)` (called from `OnModelCreating`) mirroring the existing
  private mapping methods: `HasKey(x => new { x.EventId, x.ConsumerGroup })` and a composite
  `HasIndex(x => new { x.EventName, x.ProcessedAtUtc })` to make the cleanup range-deletes cheap.
  No `HasData`.

> **Why the key includes `ConsumerGroup`:** dedup scope is *per consumer group* — each group
> independently processes every message. Keyed on `EventId` alone, adding a second consumer group to
> `common` would mean whichever group processed an event first writes the row and the second group
> **silently skips it** (real message loss, hard to diagnose). Today only `events-consumer` exists so
> both behave identically; the composite costs nothing now and removes the trap.

#### Key performance notes (Postgres)

- **No clustered-index penalty.** Unlike SQL Server, Postgres tables are heaps and the PK is an
  ordinary B-tree; other indexes do not carry the PK. So a string in the key costs only that one
  index — the `(EventName, ProcessedAtUtc)` index is unaffected.
- **`EventId` must lead.** `text` comparison is collation-aware (`strcoll`/ICU) and roughly an order of
  magnitude costlier than a 16-byte `uuid` `memcmp`. `ConsumerGroup`-first would pay that cost on
  every descent for a column with cardinality 1 (zero selectivity). `EventId`-first resolves the probe
  on the uuid alone, with the text compare only on a tie that never occurs in practice. The query
  supplies both columns so either order is valid — order for performance. It also makes the index
  usable for `WHERE event_id = ?` audit lookups across groups.
- **Optional:** `.UseCollation("C")` on `ConsumerGroup` gives byte-order comparison for an identifier
  that never needs linguistic sorting. Correct, but marginal once `EventId` leads.
- **Use UUIDv7 for `EventId`:** `Guid.CreateVersion7()` (available on `net10.0`) instead of
  `Guid.NewGuid()` in step 1. Random v4 GUIDs insert at random B-tree positions — scattered page
  splits, poor cache locality, more WAL from full-page writes. v7 is time-ordered, so inserts are
  append-mostly and rows expiring together cluster together, which also makes the step-6 purge
  cheaper. This matters more than the key width.
- **Rejected:** a surrogate `bigint` PK plus a unique index on `(EventId, ConsumerGroup)`. That is
  clustered-index-world advice; in Postgres it means maintaining *two* indexes per insert plus a
  sequence, with nothing ever querying the surrogate. Likewise, normalising `ConsumerGroup` to a
  lookup table is over-engineering for a cardinality-1 column. `varchar(n)` vs `text` makes no
  performance difference in Postgres.
- **Scale context:** per message this table takes one indexed probe and one insert inside a
  transaction costing four round trips (`BEGIN`/`SELECT`/`INSERT`/`COMMIT`), which dominate by orders
  of magnitude. At this app's throughput (1 partition, 3 workers) the key type is not measurable —
  take the ordering and UUIDv7 because they are free, not because the alternative would hurt.

### 3. Create the migration (existing pipeline applies it)
Run (per AGENTS.md):
`dotnet ef migrations add AddProcessedEventsInbox --project src/DotNetDistributedApp.Api.Data --startup-project src/DotNetDistributedApp.Api`.
No MigrationService change is needed — its `Worker` already migrates `WeatherDbContext`.
**Note:** adding a migration and changing the DB schema are "Ask First" items in AGENTS.md; this
plan is the approval vehicle.

### 4. Add the deduplication middleware (transactional inbox)
New file `src/DotNetDistributedApp.Events.Consumer/InboxDeduplicationMiddleware.cs`, implementing
`KafkaFlow.IMessageMiddleware` (same shape as `RetryDeadLetterMiddleware`), constructor-injecting the
scoped `WeatherDbContext`, `IMetricsService`, and `ILogger<InboxDeduplicationMiddleware>`, using
`[LoggerMessage]` source-generated logging. Logic:

1. If `context.Message.Value is not BaseEventPayloadDto payload` → `await next(context)`; return.
2. Wrap the unit of work in `dbContext.Database.CreateExecutionStrategy().ExecuteAsync(...)` — the
   context has `EnableRetryOnFailure()` (`ServiceCollectionExtensions.cs:29`), so an explicit
   transaction **must** run inside the execution strategy (same pattern as
   `MigrationService/Worker.cs:35`).
3. Inside the strategy: begin a transaction, run the duplicate check (below), call `next(context)`,
   record the `ProcessedEvent` row, save and commit.
   - **Do not** catch handler exceptions — let them propagate out of the strategy so the transaction
     rolls back (no inbox row) and `RetryDeadLetterMiddleware` retries / routes to DLQ.

#### Duplicate detection: fast path + constraint backstop

```csharp
await using var transaction = await dbContext.Database.BeginTransactionAsync(token);

// Hoist to locals so EF parameterises cleanly rather than translating member
// access on the captured consumer-context object.
var groupId = context.ConsumerContext.GroupId;
var eventId = payload.EventId;

var alreadyProcessed = await dbContext.ProcessedEvents.AnyAsync(
    x => x.ConsumerGroup == groupId && x.EventId == eventId,
    token
);

if (alreadyProcessed)
{
    LogDuplicateEventSkipped(eventId, payload.EventName, context.ConsumerContext.Offset);
    metricsService.ConsumeEventDuplicate(1, context.ConsumerContext.Topic, payload.EventName);
    await transaction.RollbackAsync(token);
    return;                                   // handlers skipped; offset still commits
}

await next(context);                          // handler DB writes enlist in this transaction

dbContext.ProcessedEvents.Add(
    new ProcessedEvent
    {
        EventId = eventId,
        EventName = payload.EventName,
        Topic = context.ConsumerContext.Topic,
        PartitionKey = payload.PartitionKey,
        ConsumerGroup = groupId,
        Partition = context.ConsumerContext.Partition,
        Offset = context.ConsumerContext.Offset,
        ProcessedAtUtc = DateTimeOffset.UtcNow,
    }
);

try
{
    await dbContext.SaveChangesAsync(token);
    await transaction.CommitAsync(token);
}
catch (DbUpdateException ex) when (IsUniqueViolation(ex))
{
    // Backstop: a concurrent worker committed the same event between our check and insert.
    LogConcurrentDuplicateDetected(eventId, payload.EventName);
    metricsService.ConsumeEventDuplicate(1, context.ConsumerContext.Topic, payload.EventName);
    await transaction.RollbackAsync(token);
    return;
}
```

```csharp
private static bool IsUniqueViolation(DbUpdateException exception) =>
    exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
```
(`Npgsql.PostgresErrorCodes.UniqueViolation` is a `const string` = `"23505"`, so it is valid in a
property pattern — use it instead of a magic string.)

**Why `AnyAsync`.** It compiles to `SELECT EXISTS(...)` — a primary-key index probe returning a single
bool, no entity materialisation and no change tracking (`AsNoTracking` is irrelevant here).
`FindAsync`/`CountAsync` both do strictly more work.

**It is a fast path, not the guarantee.** Postgres defaults to READ COMMITTED and `AnyAsync` takes no
lock, so two workers redelivering the same event can both see "not processed" and both proceed. The
composite primary key is the actual correctness boundary: the second `INSERT` blocks on the unique
index until the first transaction commits, then fails `23505`. `AnyAsync` exists only to make the
common case (sequential redelivery after a rebalance) cheap and exception-free.

Subtleties to respect when implementing:
- **The rollback after `23505` is mandatory, not tidiness.** Once a statement errors inside a Postgres
  transaction it is aborted and all subsequent commands fail with *"current transaction is aborted"*.
  It cannot be salvaged, and the change tracker still holds the `Added` row, so reusing the context
  would re-attempt the insert.
- **In the race case the loser's handler already ran.** Its *database* writes roll back with the
  transaction — the reason the inbox shares this `DbContext`. Non-DB side effects (HTTP calls, emails)
  would have happened twice; that is the boundary of the guarantee.
- **`EnableRetryOnFailure` re-invokes the whole lambda**, so a transient failure after `next(context)`
  re-runs the handler in a fresh transaction. The prior transaction rolled back so DB state is clean
  and the fast-path check re-runs correctly, but non-DB side effects repeat.
- **Ordering considered and rejected:** inserting the inbox row *before* calling `next` would reject a
  duplicate in one round trip having done no handler work, and would serialise concurrent workers on
  the unique index. Not chosen because it makes an exception the primary control-flow path for the
  expected duplicate case, which AGENTS.md lists under "Patterns NOT Used". Revisit if duplicate
  volume ever makes the wasted handler work matter.

#### Cancellation token propagation

`context.ConsumerContext.WorkerStopped` is the **only** cancellation token available in a KafkaFlow
middleware ("a CancellationToken that is cancelled when the worker is requested to stop"); there is no
per-message token, and `MiddlewareDelegate` carries none. `RetryDeadLetterMiddleware` already uses it
(`:28`, `:45`). Capture it once and thread it into every EF call:

```csharp
var cancellationToken = context.ConsumerContext.WorkerStopped;

var strategy = dbContext.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(
    async token =>                                    // token re-supplied on each retry attempt
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(token);
        // AnyAsync(..., token) / SaveChangesAsync(token) / CommitAsync(token) / RollbackAsync(token)
    },
    cancellationToken
);
```

Use the `ExecuteAsync(Func<CancellationToken, Task>, CancellationToken)` overload rather than the
tokenless `ExecuteAsync(Func<Task>)` + closure capture in `MigrationService/Worker.cs:36`.

- **Handlers pull it themselves.** `next(context)` has no token parameter, so any handler doing async
  work reads `context.ConsumerContext.WorkerStopped` in its own `Handle` (neither current handler uses
  `context` at all). Required by the AGENTS.md "always pass `CancellationToken`" rule.
- **Cancellation composes correctly.** An `OperationCanceledException` from an EF call rolls the
  transaction back (no inbox row) and bubbles through `RetryDeadLetterMiddleware`, which rethrows it
  rather than counting a retry or DLQ-routing (`:28-32`). It then reaches the worker's
  `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)`, which sets
  `ShouldStoreOffset = false` — **the only path in KafkaFlow that prevents offset storage** — so the
  message is redelivered after restart. Even cancellation during `CommitAsync` where the commit landed
  server-side is safe: redelivery hits the primary key and dedups.
  (Note this works *because of* that specific OCE carve-out, not because rethrowing prevents commits
  in general — it does not; see the findings section.)

Pipeline registration (`Program.cs:45–51`), outermost first:
`.AddDeserializer<JsonCoreDeserializer>()` → `.Add<RetryDeadLetterMiddleware>()` →
`.Add<ConsumerMetricsMiddleware>()` (step 7) →
`.Add<InboxDeduplicationMiddleware>(MiddlewareLifetime.Message)` → `.AddTypedHandlers(...)`.

Because `ConsumerMetricsMiddleware` short-circuits non-payload messages, the dedup middleware can
assume `context.Message.Value` is a `BaseEventPayloadDto` — omit the guard in list item 1 above if
both are implemented together.

> **`MiddlewareLifetime.Message` is mandatory, not an optimisation.** `Add<T>()` defaults to
> `MiddlewareLifetime.ConsumerOrProducer` — one instance per consumer, **shared concurrently across all
> its workers** (this consumer runs `WithWorkersCount(3)`). Since this middleware injects
> `WeatherDbContext`, the default would share one non-thread-safe `DbContext` across three workers
> (→ "a second operation was started on this context instance", interleaved transactions) and hold a
> captive pooled context whose change tracker grows by one entity per message forever. `Message`
> creates the middleware per message scope, so each message gets its own pooled context, disposed at
> scope end. `RetryDeadLetterMiddleware` is correctly left on the default — thread-safe dependencies,
> all per-message state local.

> **Handlers must be registered `Scoped` — required, and not the default.** Verified: handler
> registration defaults to `InstanceLifetime.Singleton`
> (`TypedHandlerConfigurationBuilder.cs:19`), and this project never overrides it. A singleton
> resolved through the message scope is still the **root** instance, so a handler injecting
> `WeatherDbContext` would not share the middleware's context or its transaction. Worse, the failure
> mode is environment-dependent (confirmed by probe): in **Development** `Host.CreateApplicationBuilder`
> enables `ValidateOnBuild`/`ValidateScopes`, so startup throws *"Cannot consume scoped service
> 'WeatherDbContext' from singleton"* — loud and immediate; in **Production** validation is off, so it
> silently builds a **captive root `DbContext`** shared by all 3 workers for the process lifetime
> (not thread-safe, never disposed, unbounded change tracker). Fix — one line in `Program.cs`:
>
> ```csharp
> .AddTypedHandlers(x =>
>     x.WithHandlerLifetime(InstanceLifetime.Scoped).AddHandler<SimpleEventMessageHandler>()
> )
> ```
>
> Apply it to **every** `AddTypedHandlers` call — each builds its own `TypedHandlerConfiguration`, so
> the setting does not carry across calls.
>
> **DONE.** Applied to both `AddTypedHandlers` calls. The Kafka registration moved out of `Program.cs`
> into `Events.Consumer/ServiceCollectionExtensions.cs` (`AddEventsConsumerKafka`) so the guard test can
> assert against the real configuration instead of a copy. Guarded by
> `EventsConsumerRegistrationShould.RegisterMessageHandlersAsScopedSoTheyShareTheDeduplicationTransaction`
> (verified red before the fix, naming the offending handler).

> **The design assumes one handler per message type.** `TypedHandlerMiddleware.Invoke` runs all matching
> handlers concurrently via `Task.WhenAll` (`:32`). With handlers scoped, two handlers for the same
> message type would share one `WeatherDbContext` **in parallel** — the same thread-safety failure the
> middleware lifetime fix addressed. Today each payload type maps to exactly one handler, so this is
> safe; adding a second handler for an existing type means either serialising them or giving each its
> own context (and then the single-transaction guarantee needs rethinking).
>
> **Documented + enforced.** Written up in `AGENTS.md` → *Kafka Consumer Idempotency (Constraints)*
> (auto-loaded into every agent session via `CLAUDE.md`), cross-referenced from its **Never Do** list,
> commented at the registration site, and enforced by
> `EventsConsumerRegistrationShould.MapEachPayloadTypeToExactlyOneHandler` (verified red against a
> temporary second `IMessageHandler<SimpleEventPayloadDto>`).

> Transaction-sharing fallback (only if the scoped-handler route is rejected): have the middleware begin
> the transaction and expose it via `context.Items` so handlers call
> `dbContext.Database.UseTransaction(...)` on their own context. Strictly worse — two pooled connections
> per message instead of one — so prefer the scoped handler registration.

### 5. Wire the database into the consumer
- `src/DotNetDistributedApp.Events.Consumer/DotNetDistributedApp.Events.Consumer.csproj` — add
  `<ProjectReference Include="..\DotNetDistributedApp.Api.Data\DotNetDistributedApp.Api.Data.csproj" />`
  (brings in Npgsql/EF transitively).
- `src/DotNetDistributedApp.Events.Consumer/Program.cs` — register the context via the existing
  extension: `builder.Services.AddApiDatabaseContext(builder.Configuration);` (its parameter is
  `ConfigurationManager`, which `HostApplicationBuilder.Configuration` is).
- `src/DotNetDistributedApp.AppHost/AppHost.cs` — extend the `eventsConsumer` registration
  (lines 45–48) with `.WithReference(apiDatabase).WaitForCompletion(apiDatabaseMigrations)` so the
  connection string is injected and the consumer starts only after the `processed_events` table
  exists. **Note:** this modifies the AppHost dependency graph (an "Ask First" item); part of this
  plan for approval.

### 6. Configurable cleanup / purge job (per-event-type retention)

> **Superseded by what shipped** (commit `5db4327`). The job landed as a separate
> `DotNetDistributedApp.ScheduledTasks` worker driven by Coravel rather than a `BackgroundService` inside
> `Events.Consumer`: `ProcessedWeatherEventsCleaner` + `ProcessedWeatherEventsCleanerOptions`, with a `Cron`
> expression in place of `Interval`, `PreventOverlapping`, `OnError`, and `ValidateOnStart` rejecting a
> non-positive retention. The sketch below is kept as the original design; §11 has the test plan against the
> code as built.

- New file `src/DotNetDistributedApp.Events.Consumer/EventInboxCleanupOptions.cs`:
  - `TimeSpan Interval` — how often to run.
  - `TimeSpan DefaultRetention` — applied to event names without an override.
  - `Dictionary<string, TimeSpan> RetentionByEventName` — per-event-type windows.
- New file `src/DotNetDistributedApp.Events.Consumer/EventInboxCleanupService.cs` — a
  `BackgroundService` using `PeriodicTimer(Interval)`:
  - Each tick: create a scope, resolve `WeatherDbContext`, and run **set-based** deletes with EF Core
    10 `ExecuteDeleteAsync` (no tracking, no `SaveChanges`):
    - For each `(name, retention)` in `RetentionByEventName`:
      `await db.ProcessedEvents.Where(e => e.EventName == name && e.ProcessedAtUtc < DateTimeOffset.UtcNow - retention).ExecuteDeleteAsync(ct);`
    - Catch-all for the rest:
      `await db.ProcessedEvents.Where(e => !knownNames.Contains(e.EventName) && e.ProcessedAtUtc < DateTimeOffset.UtcNow - DefaultRetention).ExecuteDeleteAsync(ct);`
  - `[LoggerMessage]` the deleted counts per event type; honor the stopping token; swallow-and-log
    transient failures so one bad tick doesn't kill the service.
- `Program.cs` — `builder.Services.Configure<EventInboxCleanupOptions>(builder.Configuration.GetSection("EventInboxCleanup"));`
  and `builder.Services.AddHostedService<EventInboxCleanupService>();`.
- `appsettings.json` — add the section, e.g.:
  ```json
  "EventInboxCleanup": {
    "Interval": "01:00:00",
    "DefaultRetention": "7.00:00:00",
    "RetentionByEventName": {
      "simple-event": "1.00:00:00",
      "failing-event": "30.00:00:00"
    }
  }
  ```

### 7. Telemetry
`src/DotNetDistributedApp.Api.Common/Metrics/IMetricsService.cs` + `MetricsService.cs` — add
`ConsumeEventDuplicate(int delta, string topic, string eventName)` backed by
`Counter<int>("events.consume_duplicate")`, following the existing `ConsumeEvent*` pattern (tags
`topic`, `event_name`). Emitted from both duplicate branches of the middleware — a dedup-specific
outcome nothing else in the pipeline can observe. Optionally add an `events.inbox_purged` counter
emitted by the cleanup service.

#### Wiring the pre-existing consume counters

`ConsumeEventSuccess`, `ConsumeEventFailed`, and `ConsumeEventUnrecognised` already exist but are never
called (`MetricsService.cs:47-54`). They do **not** belong in the dedup middleware — that would split
success from failure across pipeline layers, since the dedup middleware deliberately does not catch
handler exceptions. Wire all three from **one new `ConsumerMetricsMiddleware`**.

Two constraints pin its position, and they intersect at exactly one place:
- **`event_name` is only readable inside the deserializer.** All three signatures need
  `payload.EventName`. The deserializer passes the deserialized value inward via
  `context.SetMessage(...)`, which returns a **new** `IMessageContext` rather than mutating — so a
  middleware registered before the deserializer holds raw bytes, and still does after `next` returns.
- **The retry middleware swallows exceptions on successful DLQ routing** (`:49-51`), so anything
  outside it counts a DLQ'd poison message as a success. A truthful counter must sit inside it.

Register between `RetryDeadLetterMiddleware` and `InboxDeduplicationMiddleware`:

```csharp
.AddDeserializer<JsonCoreDeserializer>()
.Add<RetryDeadLetterMiddleware>()
.Add<ConsumerMetricsMiddleware>()
.Add<InboxDeduplicationMiddleware>(MiddlewareLifetime.Message)
.AddTypedHandlers(...)
```

```csharp
public async Task Invoke(IMessageContext context, MiddlewareDelegate next)
{
    var topic = context.ConsumerContext.Topic;

    if (context.Message.Value is not BaseEventPayloadDto payload)
    {
        LogUnrecognisedMessage(
            context.Message.Value?.GetType().FullName ?? "null",
            context.ConsumerContext.Offset
        );
        metricsService.ConsumeEventUnrecognised(1, topic, context.Message.Value?.GetType().Name ?? "unknown");
        return;                     // handlers skipped; offset commits so it isn't redelivered
    }

    try
    {
        await next(context);
        metricsService.ConsumeEventSuccess(1, topic, payload.EventName);
    }
    catch (OperationCanceledException) when (context.ConsumerContext.WorkerStopped.IsCancellationRequested)
    {
        throw;                      // shutdown, not a consume failure
    }
    catch
    {
        metricsService.ConsumeEventFailed(1, topic, payload.EventName);
        throw;                      // preserve retry/DLQ behaviour
    }
}
```

- `ConsumeEventFailed` fires once per **attempt**, matching `LogMessageHandlingFailed`'s granularity
  (`:36`), and rethrows so retry/DLQ behaviour is unchanged.
- `ConsumeEventSuccess` fires only when handlers genuinely completed — a DLQ'd message never reaches
  it. So `success + failed = attempts`, and `success` never lies about a poison message. A skipped
  duplicate counts as a success (consumed without error) with `duplicate` as an orthogonal dimension.
- `ConsumeEventUnrecognised` is tagged by CLR type since there is no `EventName` to read.
- The cancellation carve-out mirrors `RetryDeadLetterMiddleware:28-32`.

**Knock-on:** this guard means a non-payload message never reaches the dedup middleware, so the
equivalent `is not BaseEventPayloadDto` guard in step 4 becomes dead code — drop it there.

Add unit tests (`ConsumerMetricsMiddlewareShould`) for the three branches; these need no database, so
they belong in the fast unit project.

### 8. Cheap complementary win (optional, recommended)
Enable producer idempotence on the API producer (`CoreWebApplicationBuilderExtensions.cs:92`) via
KafkaFlow's producer config (`EnableIdempotence=true`) to remove producer-retry duplicates at the
source. Low risk; complements the consumer inbox.

### 9. Tests
Because the EF InMemory provider ignores transactions and unique constraints, all behavior that
depends on the database is validated against **real Postgres in the integration test project**
(`tests/DotNetDistributedApp.IntegrationTests`, Aspire + Docker) — **no SQLite/test-DB package is
added**. Cover:
- **Dedup / exactly-once:** publish the same logical event twice (same `EventId`) and assert the
  handler effect and the `processed_events` row each appear **once**, and `events.consume_duplicate`
  increments on the second delivery.
- **Rollback on failure:** a handler that throws leaves **no** `processed_events` row (transaction
  rolled back) and the message follows the retry → DLQ path.
- **Cleanup:** see §11. The shared database makes this the most constrained test in the suite, and a
  short default retention is exactly what it cannot use.
- Follow `AppHostFixture`/existing integration-test conventions; these are Docker-backed and slow, so
  keep them out of the quick unit loop.

Any genuinely DB-independent logic that can be extracted (e.g. a pure "cutoff per event name"
calculation) may still get fast unit tests in `tests/DotNetDistributedApp.Events.Consumer.Tests`
following the existing xUnit v3 / NSubstitute / AwesomeAssertions style, but the core dedup and
purge assertions live in the integration project.

### 10. Lint
Run `pwsh ./lint-fix.ps1` before committing (CSharpier + analyzers), per AGENTS.md.

### 11. Cleanup job tests (as built)

`ProcessedWeatherEventsCleaner` deletes from the inbox that the exactly-once claim rests on, so the
untested path is a destructive one: an inverted comparison deletes live rows rather than aged ones.
`ExecuteDeleteAsync` has no in-memory provider, so this runs against real Postgres in
`tests/DotNetDistributedApp.IntegrationTests` — consistent with §9's no-SQLite rule.

The shared database shapes the whole design. Postgres has a data volume, test classes run in parallel,
the out-of-process `events-consumer` writes rows throughout, and the AppHost now also runs the real
`scheduled-tasks` service on a one-minute cron with a one-day `DefaultRetention` — a second cleaner
deleting from the same table while the tests run.

**Two hazards, and the trick that defuses both:**

1. *The production cleaner must not race the seeded rows.* It deletes rows older than one day whose
   event name is not `failing-event`. So seed rows **younger than one day** — 10 minutes old is aged
   enough for a 5-minute test retention and far too young for production's catch-all.
2. *This cleaner's catch-all cannot be scoped to the test's own rows.* Its predicate is
   `!overriddenEventNames.Contains(e.EventName)` with no consumer-group filter, so a short
   `DefaultRetention` would delete other classes' rows. Defuse it by **listing the suite's real event
   names in `RetentionByEventName` with huge retentions** — naming `simple-event` and `failing-event`
   excludes them from the catch-all, leaving only the test's own unique event name in reach.

Use a unique event name per test (`$"cleaner-test-{Guid.NewGuid()}"`) so the deleted count is
deterministic.

Unlike every other test in the project these need **no polling and no barrier**: the test calls
`cleaner.Invoke()` itself and awaits it, so the deletes are complete when it returns. Do not reach for
`NotThrowAfterAsync`, and note the README's "Asserting an absence" warning does not apply for the same
reason.

**Files**

- `tests/DotNetDistributedApp.IntegrationTests/DotNetDistributedApp.IntegrationTests.csproj` — add a
  `ProjectReference` to `DotNetDistributedApp.ScheduledTasks` (currently reachable only transitively via
  the AppHost reference). CI runs `dotnet test` over the solution, so no workflow change.
- New `tests/DotNetDistributedApp.IntegrationTests/ScheduledTasks/ProcessedWeatherEventsCleanerShould.cs`.
- `tests/DotNetDistributedApp.IntegrationTests/README.md` — a short section on these tests, and a line in
  "Layer 4 — the database" recording `scheduled-tasks` as a third participant that *deletes* from
  `processed_weather_events`. That is what makes a seeded aged row vanish for reasons unrelated to a test.

**Test class**

Construct the cleaner directly — no DI container — following `RetryDeadLetterMiddlewareShould`'s
`Options.Create(...)` + NSubstitute style. Take `AppHostFixture` through the constructor as
`WeatherDeduplicationMiddlewareShould` does, and resolve `WeatherDbContext` from
`appHostFixture.CreateEventsConsumerScope()` (scoped and pooled, so never from the root provider).

xUnit creates one test-class instance per test, so a `_consumerGroup` field initialised to
`$"cleaner-test-{Guid.NewGuid()}"` is unique per test — used to scope assertions and, from
`IAsyncDisposable.DisposeAsync`, to purge that test's rows. Same reason
`AppHostFixture.PurgeEventsConsumerInboxRows` exists: don't leave a run's worth of rows in the data
volume.

*Test 1 — per-event retention override.* `DefaultRetention` is 365 days so the catch-all provably
touches nothing:

```csharp
var options = new ProcessedWeatherEventsCleanerOptions
{
    DefaultRetention = TimeSpan.FromDays(365),
    RetentionByEventName = { [eventName] = TimeSpan.FromMinutes(5) },
};
```

Seed two rows under `eventName`, at `UtcNow - 10 minutes` and `UtcNow`. After `Invoke()` the aged row is
gone, the fresh row remains, and `metricsService.Received(1).ProcessedEventDeleted(1, eventName)`.

*Test 2 — `DefaultRetention` catch-all.* The unique event name is deliberately *not* in
`RetentionByEventName`; the suite's real names are, purely to fence the catch-all off:

```csharp
var options = new ProcessedWeatherEventsCleanerOptions
{
    DefaultRetention = TimeSpan.FromMinutes(5),
    RetentionByEventName =
    {
        [new SimpleEventPayloadDto().EventName] = TimeSpan.FromDays(365),
        [new FailingEventPayloadDto().EventName] = TimeSpan.FromDays(365),
    },
};
```

Both DTOs have parameterless constructors, so read `EventName` off an instance rather than repeating
string literals. Same aged/fresh pair and row assertions, but the metric needs a matcher — stale rows
from earlier runs may be swept in the same call, so the count is not exactly 1:

```csharp
metricsService.Received(1).ProcessedEventDeleted(Arg.Is<int>(count => count >= 1), "other");
```

Three private helpers keep both tests short — `SeedInboxRow(eventName, age, cancellationToken)`
returning the new `Id`, `InvokeCleaner(options, metricsService, cancellationToken)`, and
`GetInboxRows(cancellationToken)` filtered by `_consumerGroup`. Shapes to get right:

- `ProcessedWeatherEvent` is keyed on `(Id, ConsumerGroup)`, so give each seeded row a fresh
  `Guid.CreateVersion7()` and populate `Topic` (`Topics.Common`), `PartitionKey`, `Partition` and
  `Offset` — none are nullable.
- `new ProcessedWeatherEventsCleaner(Options.Create(options), dbContext, metricsService,
  Substitute.For<ILogger<ProcessedWeatherEventsCleaner>>()) { CancellationToken = cancellationToken }` —
  the token is a settable `ICancellableInvocable` property, not a constructor parameter.
- `AppHostFixture.CreateCancellationToken()` for the token; `AsNoTracking()` on the read-back.

Follow the project's conventions: `[ClassUnderTest]Should`, method names as sentences without
underscores, no Arrange/Act/Assert comments, AwesomeAssertions throughout.

**Confirm each test fails for the right reason** (§9's TDD convention, and because "the row is gone"
passes trivially if the row was never seeded):

- Invert the cleaner's comparison (`ProcessedAtUtc >`) — both tests should fail on the fresh row
  surviving.
- Point Test 2's `RetentionByEventName` at its own unique event name — the catch-all no longer reaches
  the row, so it should fail.

Then re-run the suite twice; the second run catches an accidental dependence on an empty table.
Afterwards query `processed_weather_events` for `consumer_group LIKE 'cleaner-test-%'` and expect
nothing — if rows remain, xUnit is not disposing the test class and the purge belongs in the test
bodies.

---

## Pre-existing data-loss findings — **FIXED**

> **Both findings were re-verified against the real KafkaFlow 4.2.0 source and then fixed.** Two claims below
> were wrong on mechanism and are corrected inline. Constraints are recorded in `AGENTS.md` →
> *Kafka Consumer Data Loss (Constraints)*, and guarded by `EventsConsumerRegistrationShould`,
> `RetryDeadLetterMiddlewareShould` and the new integration test
> `UnreadableMessageDeadLetteringShould`.

Surfaced while placing the consume metrics and **verified against KafkaFlow 4.2.0 source**. Neither was
caused by the idempotency change nor fixed by it.

The root cause of both is `ConsumerWorker.ProcessMessageAsync`:

```csharp
await _middlewareExecutor.Execute(context, _ => Task.CompletedTask).ConfigureAwait(false);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    context.ConsumerContext.ShouldStoreOffset = false;
}
catch (Exception ex)          // <- every other exception is swallowed and logged
{
    await _globalEvents.FireMessageConsumeErrorAsync(new MessageErrorEventContext(context, ex));
    _logHandler.Error("Error processing message", ex, new { ... });
}
finally
{
    if (context.ConsumerContext.AutoMessageCompletion)   // <- true by default
    {
        context.ConsumerContext.Complete();              // <- offset stored even after a failure
    }
    ...
}
```

**Finding 1 — `RetryDeadLetterMiddleware`'s DLQ-failure path did the opposite of what it intended.**
Its log message read *"rethrowing so offset is not committed"* and it rethrew when the DLQ produce failed.
Rethrowing does **not** prevent offset storage: the worker catches it and `finally` completes the
message. So when the DLQ produce failed, the message was **lost** — exactly the case the code
was written to protect. *(Correction: "silently" was overstated — two error logs and a `MessageConsumeError`
event fire. Also, the likeliest trigger was not a broker outage but the `ArgumentException` the method threw
itself when the value was not a `BaseEventPayloadDto`, inside the same `try`.)*

**Fixed** by setting `context.ConsumerContext.ShouldStoreOffset = false` before rethrowing. The cost, which the
original finding did not name: `OffsetManager` commits contiguously, so a context that is never marked processed
pins the partition's committed offset permanently and later offsets accumulate in memory until a restart replays
them (the inbox absorbs the replay). Correct over losing the message, but it makes a failing DLQ produce an
incident rather than a warning — the log message now says so.

**Finding 2 — deserialization failures were silently dropped with no DLQ.**
`AddDeserializer<JsonCoreDeserializer>()` was registered first and was therefore the *outermost*
middleware, with `RetryDeadLetterMiddleware` inside it. `DeserializerConsumerMiddleware` does not catch
(confirmed — no try/catch around `DeserializeAsync`), so a malformed payload threw past the retry middleware:
no retry, no DLQ, offset stored anyway, only a log line and a `MessageConsumeError` global event.

*(Correction: an unresolvable `Message-Type` header does **not** throw. `DefaultTypeResolver` returns `null` when
the header is missing, and `Type.GetType` returns `null` for a type this process cannot load, so
`DeserializerConsumerMiddleware` takes its `if (messageType is null) return;` branch — no exception, no log, no
`MessageConsumeError` event, `next` never called, offset stored. Strictly quieter than the malformed-payload case,
and invisible to the `MessageConsumeError` global event this section suggested as central coverage.)*

**Fixed** in three parts:
- `.Add<RetryDeadLetterMiddleware>()` now precedes `.AddDeserializer<...>()`, so retry/DLQ wraps the deserializer.
- `SendToDeadLetterAsync` forwards the raw bytes, raw key and original headers rather than casting to
  `BaseEventPayloadDto`. Consequence of the new position: the middleware never sees a deserialized payload at all,
  because the deserializer passes it inward on a *new* `IMessageContext`. The dead letter is now a byte-for-byte
  copy plus one header — higher fidelity than re-serializing a DTO, and the only shape that works for a payload
  that never deserialized.
- The DLQ producer lost its `AddSerializer<JsonCoreSerializer>()`, which would otherwise base64 those bytes into a
  JSON string and overwrite `Message-Type` with `System.Byte[]`.
- New `StrictMessageTypeResolver` throws instead of returning `null`, closing the silent path above.
  **Trade-off worth knowing:** a payload type this process cannot load is now a dead letter rather than an ignored
  message. That is right for a version skew or a renamed type (nothing is lost, and the DLQ is replayable), but it
  means the integration suite now dead letters its own `TestMessage` from the out-of-process service every run. A
  payload type with *no handler* is still ignored harmlessly — `TypedHandlerMiddleware` always calls `next`.

Retries are deliberately left uniform: a deserialization failure is deterministic, so it burns the configured
backoff before dead lettering. Discriminating by exception type was rejected as more brittle than it is worth;
revisit if poison volume ever makes the wasted attempts matter.

Related note for step 7: because the worker swallows failures, `events.consume_failed` from
`ConsumerMetricsMiddleware` becomes the main *metric* signal that a handler failed. With retry/DLQ now
outermost, a `ConsumerMetricsMiddleware` placed inside it still cannot see deserialization failures — but those
are dead lettered now rather than dropped, so the DLQ is the signal for them.

`MessageConsumeError` is **not** the central catch-all this section suggested: it fires only for exceptions that
reach the worker, so it never saw the unresolvable-`Message-Type` case (which threw nothing) and no longer sees
deserialization failures either (the retry middleware handles them).

### Also corrected while verifying: the duplicate-source mechanism in *Context* §1

"librdkafka *commits* it later on `auto.commit.interval.ms`" is wrong. `ConsumerConfigurationBuilder.Build`
(`:254-256`) overwrites `EnableAutoOffsetStore`, `EnableAutoCommit` and `AutoCommitIntervalMs` whatever the caller
sets. `IsStopTheWorldStrategy` returns `true` for a `null` `PartitionAssignmentStrategy` — which is what this repo
has — so KafkaFlow sets `EnableAutoCommit = false` and librdkafka never commits. Commits come solely from
KafkaFlow's own `OffsetCommitter` `Timer` on KafkaFlow's `AutoCommitInterval` (default 5s), calling
`_consumer.Commit(...)`. The conclusion — a ~5s stored-but-uncommitted window causing redelivery — holds, and the
interval even coincides; only the mechanism and the knob were wrong. Two knock-ons, both now fixed:
`EnableAutoCommit = false` in `ServiceCollectionExtensions` was **inert** (overwritten, coincidentally to the same
value) and has been removed with a comment naming `WithAutoCommitIntervalMs` as the only real knob.

Likewise "the **only** path that sets `ShouldStoreOffset = false`" is not literal: 4.2.0 also writes it in
`BatchConsumeMiddleware` (batching unused here) and in `Discard()`, called when a worker drains its buffer at
shutdown. For messages that entered the pipeline the claim holds, so the cancellation argument in step 4 stands.

---

## Verification

- **Unit:** `dotnet test --project tests/DotNetDistributedApp.Events.Consumer.Tests` — middleware and
  cleanup tests pass.
- **End-to-end (Docker):** `dotnet run --project src/DotNetDistributedApp.AppHost`; POST twice to
  `/v1/events/simple-event`. Confirm each *distinct* `EventId` is handled once, and inspect
  `processed_events` in pgAdmin — one row per processed event, populated audit columns. Force a
  redelivery (restart `events-consumer` before its offset commits) and confirm the message is skipped
  and `events.consume_duplicate` increments in the dashboard.
- **Cleanup:** temporarily set a short `Cron` and retention in
  `src/DotNetDistributedApp.ScheduledTasks/appsettings.json`, insert aged rows, and confirm
  `scheduled-tasks` purges per event name as configured (and logs the counts). Note it already runs on a
  one-minute cron during an integration test run.
- **Regression:** confirm `FailingEventMessageHandler` still routes to `common-dlq` after retries and
  that **no** `processed_events` row is written for a DLQ'd message (each attempt rolled back).
