# Integration Tests

Aspire + Docker tests that run the whole app: real Postgres, real Kafka, real Valkey, real services.
`AppHostFixture` is an assembly-level fixture (`[assembly: AssemblyFixture(...)]`), so the `AppHost` starts
once and every test class shares it. Startup dominates the run — keep these out of the quick feedback loop
and run the unit projects first.

Most of this document is about one thing that surprises people: **the fixture holds two separate
`IServiceProvider`s, each with its own `IKafkaBus`.** If you only need to hit an HTTP endpoint, none of it
matters. If you are writing anything that touches the events consumer, read on.

## Why there are two Kafka hosts

`AppHostFixture.ConfigureKafkaServices` builds the original one: a producer plus a deliberately minimal
consumer — deserializer, then `DelegatingTestMessageHandler` wrapping an NSubstitute mock. That is all
`EventsServiceShould` needs, because it is testing the *producer*. It also carries a second consumer, on
`common-dlq`, that feeds `DeadLetterRecorder` — see [Asserting an absence](#asserting-an-absence).

It cannot serve the deduplication tests. It has no `RetryDeadLetterMiddleware`, no
`WeatherDeduplicationMiddleware`, no `WeatherDbContext`, and its handlers are not registered `Scoped`.
Nothing it does touches the transactional inbox.

The real pipeline *is* running during a test run — as the out-of-process `events-consumer` resource under
Aspire — but from there its metrics and its in-transaction handler effects are not observable without
scraping OTLP. So `AppHostFixture.ConfigureEventsConsumerServices` builds a second host that runs the real
`AddEventsConsumerKafka` registration **in-process**, giving tests direct access to the inbox rows and to
`IMeterFactory`.

The separation then happens at three independent layers. They are easy to conflate.

## Layer 1 — DI: two containers

`new ServiceCollection().BuildServiceProvider()` produces a closed object graph. There is no global registry
and no ambient "current" provider, so two containers share no registrations, no singletons and no scopes.

Two rather than one is a constraint, not a preference. `AddKafka` registers a singleton `KafkaFlowConfigurator`
and writes every middleware, handler and producer registration into *that* `IServiceCollection`
(`MicrosoftDependencyConfigurator(services)`). `CreateKafkaBus` then resolves a **single** configurator:

```csharp
provider.GetRequiredService<KafkaFlowConfigurator>().CreateBus(resolver);
```

Two `AddKafka` calls in one container would leave two configurator singletons, and `GetRequiredService`
returns the last registered — the first cluster becomes silently unreachable. Do not merge the containers.

There are practical reasons too: the second container needs `WeatherDbContext`, `IMetricsService` and
`ValidateScopes`, none of which the first wants.

## Layer 2 — KafkaFlow: each bus is built from its own container

```
_kafkaServiceProvider ──CreateKafkaBus()──▶ _kafkaBus
  AddProducer<EventsService>  → common          (IMessageProducer<EventsService>)
  AddConsumer  group "integration-tests-{guid}"
    deserializer → DelegatingTestMessageHandler → NSubstitute mock

_eventsConsumerServiceProvider ──CreateKafkaBus()──▶ _eventsConsumerKafkaBus
  AddProducer<DlqProducer>    → common-dlq      (IMessageProducer<DlqProducer>, no serializer)
  AddConsumer  group "integration-tests-events-consumer-{guid}"
    RetryDeadLetter → deserializer → WeatherDeduplication → typed handlers
  + WeatherDbContext, IMetricsService, IMeterFactory
```

Note the retry middleware is **outside** the deserializer, and the DLQ producer has **no serializer**. Both are
load-bearing: see [Dead lettering a message that cannot be read](#dead-lettering-a-message-that-cannot-be-read).

Both buses are started by hand (`CreateKafkaBus().StartAsync()`). `AddKafkaFlowHostedService` registers an
`IHostedService` that would normally do it, but nothing runs hosted services in a bare container. Both are
stopped and disposed independently in `DisposeAsync`.

## Layer 3 — Kafka: consumer groups are what actually separate them

This is the layer people expect DI to be doing, and it is not. Both buses connect to the **same broker** and
subscribe to the **same `common` topic**. What keeps them apart is Kafka's own rule: every consumer *group*
receives its own copy of every message, and within a group each partition goes to exactly one member.

Three groups are live during a run:

| Group                                        | Where                      | Pipeline                        |
|----------------------------------------------|----------------------------|---------------------------------|
| `integration-tests-{guid}`                   | in-process, provider #1    | deserializer + mock handler     |
| `integration-tests-events-consumer-{guid}`   | in-process, provider #2    | the real registration           |
| `events-consumer`                            | out-of-process, Aspire     | the real registration           |

The in-process pipeline **must** have its own group id. Joining `events-consumer` would make it a second
member of a group reading a one-partition topic, so only one of the two would ever be assigned the partition
and see any given message. That is why `AddEventsConsumerKafka` takes an optional
`Action<IConsumerConfigurationBuilder>` — the fixture uses it to override the group id and to set
`AutoOffsetReset.Earliest` (the default `Latest` loses anything produced before partition assignment
completes; see the comment block in `ConfigureKafkaServices`).

**All three groups see every message.** Consequences worth knowing:

- The real-pipeline consumer also consumes `EventsServiceShould`'s `TestMessage` and writes an inbox row for
  it. It can, because `TestMessage` lives in the test assembly, which is loaded in-process.
- **The out-of-process service cannot, and now dead letters it.** `Type.GetType("...TestMessage,
  DotNetDistributedApp.IntegrationTests")` returns `null` there, and `StrictMessageTypeResolver` throws on
  that rather than letting `DeserializerConsumerMiddleware` drop the message silently. So every run puts one
  `TestMessage` on `common-dlq` under the `events-consumer` group, after that service has spent its full
  production backoff (2s/4s/8s) retrying it. Expected noise, not a failure — but it is why the out-of-process
  service looks busy for ~15s during a run, and why a test message can queue behind it on a shared worker.
- A payload type with no registered handler is still harmless. `TypedHandlerMiddleware` invokes
  `OnNoHandlerFound` (a no-op by default) and then always calls `next`. "No handler for this type" and "cannot
  load this type" are different things: the first is ignored, the second is a defect and gets dead lettered.

## Layer 4 — the database

Both real pipelines write to the same `processed_weather_events` table. `ProcessedWeatherEvent` is keyed on
`(Id, ConsumerGroup)`, so the same `EventId` seen by two groups is two distinct rows.

> **Scope every inbox assertion to `AppHostFixture.EventsConsumerGroupId`.**
>
> This is the one rule you can break and still get a passing test today. Postgres has a data volume, so rows
> outlive the run that wrote them, and the real `events-consumer` is writing its own rows the whole time.
> Unscoped queries will eventually see both.

`AppHostFixture.PurgeEventsConsumerInboxRows` deletes the fixture's own group's rows at dispose, after the
in-process bus has stopped. It is deliberately **not** a before-each clear: test classes run in parallel
(nothing disables xUnit's default), so a broader delete would remove rows another class is still waiting on,
and it would strip the real service of the records it uses to recognise events it has already handled.

## Choosing a host in a test

There is no routing. Each fixture member is hard-wired to one provider:

| Member                            | Provider | Gives you                                              |
|-----------------------------------|----------|--------------------------------------------------------|
| `GetMessageProducer<T>()`         | #1       | `IMessageProducer<EventsService>` / `<UnreadableEventProducer>` |
| `GetMessageHandler<T>()`          | #1       | the NSubstitute `IMessageHandler<TestMessage>`          |
| `WaitForDeadLetteredEvent()`      | #1       | a barrier: provider #2's pipeline gave up on an event   |
| `WaitForUnreadableDeadLetter()`   | #1       | the same, for a message that never deserialized         |
| `EventsConsumerServices`          | #2       | `IMeterFactory`, and anything else the pipeline has     |
| `CreateEventsConsumerScope()`     | #2       | a scope to resolve `WeatherDbContext` from              |
| `EventsConsumerGroupId`           | #2       | the value to filter inbox assertions by                 |

Crossing them is normal. `WeatherDeduplicationMiddlewareShould` **produces** through provider #1 and
**asserts** through provider #2's `WeatherDbContext`. Producing is just putting bytes on the topic; which
container owns the producer is irrelevant once they are there, and all three groups then pick the message up.

`WeatherDbContext` is scoped and pooled, so it must come from `CreateEventsConsumerScope()`, never the root
provider.

### Gotcha: `IMessageProducer<DlqProducer>`

It is registered only in provider #2. `GetMessageProducer<DlqProducer>()` reads provider #1 and will throw.
A test that needs to observe the dead-letter path has to resolve from `EventsConsumerServices`, or add its own
consumer on `common-dlq`.

## Writing a new events-consumer test

1. Build a payload and keep the instance — `BaseEventPayloadDto.EventId` defaults to a fresh
   `Guid.CreateVersion7()`, and reusing the same instance is how you produce a *duplicate*.
2. Produce with `GetMessageProducer<EventsService>().ProduceAsync(Topics.Common, key, payload)`.
3. Poll for the effect. Delivery is asynchronous, so wrap assertions in
   `FluentActions.Awaiting(...).Should().NotThrowAfterAsync(30.Seconds(), 250.Milliseconds())` rather than
   sleeping.
4. Filter every query by `EventsConsumerGroupId` **and** the payload's `EventId`.
5. For metrics, create the `MetricCollector<int>` from `EventsConsumerServices`'s `IMeterFactory` *before*
   producing. Use `MetricsService.MeterName` and the instrument name (e.g. `events.consume_duplicate`).

Retry timings are shortened in the fixture (1 retry, flat 200ms) so the retry → DLQ path completes inside a
test's patience. Production backs off 2s/4s/8s.

## Asserting an absence

Step 3 above only works for effects that *appear*. `NotThrowAfterAsync` returns at the first poll where its
assertion holds, so wrapping `BeEmpty()` in it asserts nothing at all: "no inbox row" is true before the
consumer has even been assigned the partition, and the test passes however broken the pipeline is.

To assert that something was *not* written you need a barrier — a signal proving the pipeline is finished with
the event — and then a single unpolled assertion. The failure path writes nothing to the database by design, so
the barrier has to come from Kafka: `await appHostFixture.WaitForDeadLetteredEvent(payload.EventId, ct)`.

Why that is sound, and why each piece is load-bearing:

- **Dead lettering is terminal.** `RetryDeadLetterMiddleware` produces to `common-dlq` only after exhausting
  retries, and completes the message afterwards, so nothing further happens to the event.
- **The rollback strictly precedes it.** `WeatherDeduplicationMiddleware`'s `await using` transaction is
  disposed — and so rolled back — while the handler's exception unwinds, before the retry middleware's `catch`
  runs. Once the dead letter exists, the inbox is final for that event.
- **The group id matters.** All three consumer groups run the pipeline and each dead letters its own copy, so
  the recorder filters on `DeadLetterHeaders.ConsumerGroup`, a header `RetryDeadLetterMiddleware` sets from
  `context.ConsumerContext.GroupId`. The `events-consumer` service can beat the in-process pipeline to the DLQ;
  waiting on *its* dead letter would put the barrier back before the rollback it is meant to prove.
- **The recorder is a separate consumer.** Typed handlers are configured per consumer, so
  `DeadLetteredFailingEventMessageHandler` only ever sees `common-dlq`. A handler that also fired on the
  original `common` message would prove nothing. Note that the events-consumer pipeline itself must never
  subscribe to `common-dlq` — it would deserialize its own dead letters and fail them all over again.

One asymmetry to know when a test like this goes red: a middleware that records the inbox row *despite* the
handler throwing makes the retry find the event already processed, skip the handler and succeed — so the event
is never dead lettered and you get a barrier timeout rather than a failed `BeEmpty()`. `WaitForDeadLetteredEvent`
throws a `TimeoutException` spelling that out.

## Dead lettering a message that cannot be read

`UnreadableMessageDeadLetteringShould` covers the two ways a message can be undeliverable before any handler is
reached. Both used to end in silent data loss, and this is the only test in the suite that proves they no
longer do — so if you are changing the consumer pipeline order, read this before you do.

Three registration details make it work, and breaking any one of them turns both tests into a
`WaitForUnreadableDeadLetter` timeout rather than a clear failure:

1. **`RetryDeadLetterMiddleware` is registered before — and so wraps — the deserializer.**
   `DeserializerConsumerMiddleware` has no `try`/`catch`. With the deserializer outermost, a malformed payload
   throws past retry/DLQ into `ConsumerWorker.ProcessMessageAsync`, which logs the exception, swallows it, and
   its `finally` stores the offset anyway. No retry, no dead letter, message gone.
2. **The deserializer uses `StrictMessageTypeResolver`, not KafkaFlow's `DefaultTypeResolver`.** The default
   returns `null` when `Message-Type` is missing or names an unloadable type, and the deserializer answers
   `null` with a bare `return` — no handler, no exception, no log, no `MessageConsumeError` event, offset
   stored. Quieter than a malformed payload, which at least throws.
3. **The DLQ producer has no serializer middleware.** Sitting outside the deserializer,
   `RetryDeadLetterMiddleware` only ever holds raw bytes (`DeserializerConsumerMiddleware` passes the
   deserialized value inward on a *new* `IMessageContext`), so it forwards the original bytes, key and headers
   with one header added. A `JsonCoreSerializer` on that producer would base64 the bytes into a JSON string and
   overwrite `Message-Type` with `System.Byte[]`.

Consequence for observing them: a poison dead letter cannot reach a typed handler either, so
`DeadLetterRecorder` and `DeadLetteredFailingEventMessageHandler` are no use.
`UnreadableDeadLetterRecordingMiddleware` sits *before* the deserializer on the fixture's `common-dlq`
consumer and records raw bytes into `UnreadableDeadLetterRecorder`, keyed by `(consumer group, payload
string)`. Keying on the exact payload is deliberate: it makes the wait itself an assertion that the bytes
round-tripped untouched.

Producing them needs `GetMessageProducer<UnreadableEventProducer>()` — provider #1's serializer-free producer,
which takes a `byte[]` and puts it on `common` verbatim.

## Cancellation tokens

Three options exist here and they are not interchangeable.

**`TestContext.Current.CancellationToken` is the default.** xUnit signals it when the *run* is aborted (Ctrl+C,
or `ITestContext.CancelCurrentTest`), and it has no deadline of its own — on a healthy run it is never
cancelled. That is what you want for anything already bounded: EF queries, `SaveChangesAsync`, `HttpClient`
calls. Its job is to abort in-flight work when the run is going away.

**`AppHostFixture.CreateDeadline()` is for a wait that would otherwise hang forever** — `WaitForDeadLetteredEvent`,
or any await on a *signal* rather than a round trip. It is a 60s deadline linked to the token above, so it covers
both cases. It returns the source, so dispose it: `using var deadline = AppHostFixture.CreateDeadline();`.

The deadline is load-bearing at its one call site. `WaitForDeadLetteredEvent` awaits a `TaskCompletionSource` and
nothing else bounds it, and the `TimeoutException` above — the message that explains the "recorded the row anyway"
failure mode — only appears if the token trips. `[Fact(Timeout = …)]` is not a substitute: xUnit races the test
against `Task.Delay`, throws its own `TestTimeoutException` and never awaits the test's task, so that message is
discarded. (`WaitForDeadLetteredEvent` skips its `TimeoutException` when the ambient token is cancelled, so an
aborted run is not misreported as a timeout.)

**`AppHostFixture.CreateCancellationToken()` is the unlinked variant, for the fixture's own lifecycle only.**
`DisposeAsync` must not inherit an already-cancelled token, or `PurgeProcessedWeatherEvents` silently skips after
an aborted run and the run's rows survive in the volume.

Two things that look like safety nets and are not. `NotThrowAfterAsync` catches *every* exception while polling
and its own poll delay is uncancellable, so a cancelled token inside the loop is swallowed and retried until the
wait time expires. And xUnit1051 only fires when a token argument is *omitted or `default`* — it will not tell
you that a token is the wrong one, and it says nothing about a call that binds to a `params` overload with no
token parameter at all (`DbSet.AddRangeAsync(events)` is exactly that).
