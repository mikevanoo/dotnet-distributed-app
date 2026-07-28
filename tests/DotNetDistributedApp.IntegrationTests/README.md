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
`EventsServiceShould` needs, because it is testing the *producer*.

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
  AddProducer<DlqProducer>    → common-dlq      (IMessageProducer<DlqProducer>)
  AddConsumer  group "integration-tests-events-consumer-{guid}"
    deserializer → RetryDeadLetter → WeatherDeduplication → typed handlers
  + WeatherDbContext, IMetricsService, IMeterFactory
```

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
- The out-of-process service cannot: `DefaultTypeResolver` calls
  `Type.GetType("...TestMessage, DotNetDistributedApp.IntegrationTests")`, gets `null`, and
  `DeserializerConsumerMiddleware` returns **without calling `next`** — silently dropped.
- A payload type with no registered handler is harmless. `TypedHandlerMiddleware` invokes
  `OnNoHandlerFound` (a no-op by default) and then always calls `next`.

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

| Member                          | Provider | Gives you                                            |
|---------------------------------|----------|------------------------------------------------------|
| `GetMessageProducer<T>()`       | #1       | `IMessageProducer<EventsService>`                     |
| `GetMessageHandler<T>()`        | #1       | the NSubstitute `IMessageHandler<TestMessage>`        |
| `EventsConsumerServices`        | #2       | `IMeterFactory`, and anything else the pipeline has   |
| `CreateEventsConsumerScope()`   | #2       | a scope to resolve `WeatherDbContext` from            |
| `EventsConsumerGroupId`         | #2       | the value to filter inbox assertions by               |

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

## Naming

The `KAFKA-IDEMPOTENCY-PLAN.md` design document uses earlier names. The code has:

| Plan                          | Code                             |
|-------------------------------|----------------------------------|
| `ProcessedEvent`              | `ProcessedWeatherEvent`          |
| `processed_events`            | `processed_weather_events`       |
| `InboxDeduplicationMiddleware`| `WeatherDeduplicationMiddleware` |
| `event_id` column             | `id`                             |
