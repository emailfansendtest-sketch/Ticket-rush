# TicketRush — Implementation Notes

Practical checklist of .NET decisions, pitfalls and acceptance criteria for building TicketRush.
Each section maps to a roadmap phase. Check versions and license terms when starting a phase.

> **Core invariant:** a seat is never sold twice. PostgreSQL is the source of truth; Redis is only an accelerator.

---

## Contents

1. [Licensing](#1-licensing)
2. [Solution setup](#2-solution-setup)
3. [Correctness: preventing overselling](#3-correctness-preventing-overselling) — Phase 1
4. [Messaging, outbox and saga](#4-messaging-outbox-and-saga) — Phase 2
5. [State across multiple replicas](#5-state-across-multiple-replicas) — Phase 3
6. [Redis](#6-redis) — Phase 3
7. [Async, threading and background work](#7-async-threading-and-background-work)
8. [HTTP between services and resilience](#8-http-between-services-and-resilience) — Phase 2
9. [Native AOT](#9-native-aot) — Phase 4
10. [Memory management](#10-memory-management) — Phase 4
11. [Identity (Keycloak)](#11-identity-keycloak)
12. [Observability and health](#12-observability-and-health)
13. [Configuration and Aspire](#13-configuration-and-aspire)
14. [Testing](#14-testing)
15. [Interview talking points](#15-interview-talking-points)
16. [Learning order](#16-learning-order)

---

## 1. Licensing

Several popular .NET libraries changed to commercial licenses in 2025. Choose on purpose and record the decision in an ADR.

| Library | Status (verify current terms) | Decision |
|---|---|---|
| MassTransit | v8 is Apache 2.0; v9+ is commercial | Pin v8, or use Wolverine / Rebus |
| MediatR, AutoMapper | Commercial from 2025 | Not needed: plain handlers, explicit mapping |
| FluentAssertions | v8 is commercial | Shouldly or AwesomeAssertions |
| Redis server | License changed (RSAL/SSPL, later AGPL option) | Valkey is a drop-in alternative |

- [ ] ADR written: messaging library choice and license
- [ ] ADR written: Redis vs. Valkey

---

## 2. Solution setup

- [ ] .NET 10 (LTS), `.slnx` solution format
- [ ] `Directory.Build.props`: `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
- [ ] `Directory.Packages.props` (Central Package Management) — one version per package across all services
- [ ] `.editorconfig` + .NET analyzers (optional: Meziantou, SonarAnalyzer)
- [ ] `BuildingBlocks.Contracts` contains **only** message/DTO records; changes are additive only
- [ ] No domain or EF entities shared between services

---

## 3. Correctness: preventing overselling

**Phase 1.** This is the heart of the project — get it right before anything else.

### Database rules

```csharp
// SeatAllocationConfiguration.cs
builder.Property<uint>("Version").IsRowVersion();          // maps to PostgreSQL xmin (Npgsql)
builder.HasIndex(x => new { x.EventId, x.SeatId }).IsUnique();
```

### Map database conflicts to domain results (not HTTP 500)

```csharp
try
{
    await db.SaveChangesAsync(ct);
}
catch (DbUpdateConcurrencyException)
{
    return HoldResult.Conflict;      // someone else changed the row
}
catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
{
    return HoldResult.AlreadySold;   // unique index stopped a double sale
}
```

### Pitfalls

- [ ] Never test concurrency with EF InMemory or SQLite — use real PostgreSQL via Testcontainers
- [ ] `DbContext` is not thread-safe: one per request/message; never inject into singletons or `BackgroundService` (use `IServiceScopeFactory`)
- [ ] Use UTC everywhere; prefer `DateTimeOffset` (Npgsql `timestamptz` requires `DateTime.Kind = Utc`)
- [ ] Reconciliation job for Redis/PostgreSQL disagreement (expired TTL, Redis restart) — PostgreSQL wins

### Acceptance criteria

- [ ] Test: 1,000 parallel buyers compete for 100 seats → exactly 100 sold, 0 duplicates
- [ ] Conflicts return 409 with a domain error, never 500

---

## 4. Messaging, outbox and saga

**Phase 2.**

- [ ] Delivery is **at-least-once**: every consumer is idempotent (inbox / dedup table / natural unique key)
- [ ] Transactional outbox (MassTransit EF outbox): entities and outgoing messages saved in one transaction
- [ ] Never publish before `SaveChanges` outside the outbox
- [ ] Saga state uses optimistic concurrency (events for one order can arrive in parallel)
- [ ] Retry with exponential backoff + jitter → error/dead-letter queue
- [ ] Documented way to inspect and replay failed messages (shown in demo)
- [ ] Saga handles out-of-order events (e.g. `PaymentAuthorized` before `HoldConfirmed`)
- [ ] Hold expiry via scheduler (or Redis TTL + sweeper) triggers compensation

### Acceptance criteria

- [ ] Saga tests with MassTransit test harness (`AddMassTransitTestHarness`) cover success, payment failure, timeout
- [ ] Killing the Payment pod mid-checkout loses no orders; the saga resumes

---

## 5. State across multiple replicas

**Phase 3.** Most common scale-out bugs in ASP.NET Core.

| Concern | Default (wrong at scale) | Fix |
|---|---|---|
| Data Protection keys | Per instance → tokens from pod A fail on pod B | `PersistKeysToStackExchangeRedis(...)` (or DB) + shared application name |
| Rate limiter (`AddRateLimiter`) | Per instance → N pods allow N× limit | Enforce at gateway, or Redis-backed limiter |
| `IMemoryCache` / output cache | Per instance | HybridCache (memory + Redis) or Redis output-cache store |
| SignalR | Connections live on one pod | Redis backplane + sticky sessions (or WebSockets-only with `SkipNegotiation = true`) |
| EF migrations at startup | Replicas race | `dotnet ef migrations bundle` run as a Kubernetes Job |
| Scheduled jobs | Run on every pod | Broker scheduler, or Redis TTL + distributed lock |

### Acceptance criteria

- [ ] 3 replicas: waiting-room token issued by one pod is accepted by any other
- [ ] Seat-map update on one pod reaches clients connected to all pods
- [ ] Global rate limit holds regardless of replica count

---

## 6. Redis

- [ ] One `ConnectionMultiplexer` per app, registered as singleton
- [ ] Lua scripts prepared once (`LuaScript.Prepare` → `LoadedLuaScript`), run via `ScriptEvaluateAsync`
- [ ] Hash tags keep an event's seats in one cluster slot: `seat:{eventId}:A12` (avoids `CROSSSLOT`)
- [ ] Timeouts configured; `AbortOnConnectFail = false`
- [ ] Defined behavior when Redis is down: **fail closed** (no sales) — documented in an ADR

### Atomic hold script

```lua
-- KEYS: seat:{eventId}:<seatId> ; ARGV[1] = holdId, ARGV[2] = ttlSeconds
for i, key in ipairs(KEYS) do
  if redis.call('EXISTS', key) == 1 then return 0 end
end
for i, key in ipairs(KEYS) do
  redis.call('SET', key, ARGV[1], 'EX', ARGV[2])
end
return 1
```

---

## 7. Async, threading and background work

- [ ] No `.Result` / `.Wait()` (thread-pool starvation under load)
- [ ] `CancellationToken` passed everywhere; `HttpContext.RequestAborted` for SignalR/long requests
- [ ] `BackgroundService.ExecuteAsync` catches, logs and loops (an unhandled exception stops the host since .NET 8)
- [ ] `TimeProvider` instead of `DateTime.UtcNow`; tests use `FakeTimeProvider`
- [ ] IDs via `Guid.CreateVersion7()` (time-ordered, efficient index inserts)

---

## 8. HTTP between services and resilience

**Phase 2.**

- [ ] `IHttpClientFactory` with typed clients; never `new HttpClient()` per call
- [ ] `Microsoft.Extensions.Http.Resilience` (`AddStandardResilienceHandler()`, Polly v8) — included via Aspire ServiceDefaults
- [ ] Retry **only idempotent** calls
- [ ] `Idempotency-Key` header on `POST /orders` and payment calls
- [ ] Idempotency-key table has a unique constraint (parallel duplicate requests race)

### Acceptance criteria

- [ ] Same `Idempotency-Key` sent 10× in parallel → one order, one payment

---

## 9. Native AOT

**Phase 4.** Inventory service only.

- [ ] Minimal APIs only (no MVC controllers)
- [ ] System.Text.Json source generation for all DTOs
- [ ] Hot path uses Npgsql directly (or Dapper.AOT) — EF Core AOT support is limited/experimental
- [ ] No MassTransit / reflection-heavy libraries in the AOT service
- [ ] Trimming and AOT warnings treated as errors
- [ ] ADR: "AOT where startup time matters, JIT where libraries need it"

### Acceptance criteria

- [ ] Startup time and RSS of AOT vs. JIT build recorded in `docs/memory.md`

---

## 10. Memory management

**Phase 4.** Measure first, then optimize.

### Containers and GC

- [ ] Kubernetes memory limits always set (GC heap hard limit defaults to ~75% of the container limit)
- [ ] Compare Server GC with DATAS (default from .NET 9) vs. DATAS off vs. Workstation GC for small workers
- [ ] Optionally tune `DOTNET_GCHeapHardLimitPercent`

### Techniques

- [ ] Seat map as a bitset (`ulong[]`, one bit per seat) — 20k seats ≈ 2.5 KB
- [ ] Delta seat-map updates serialized with `Utf8JsonWriter` into a pooled buffer
- [ ] `ArrayPool<T>` / `ObjectPool<T>` for batch buffers and builders
- [ ] `FrozenDictionary` for venue/price lookups loaded at startup
- [ ] `Span<T>`-based seat-ID parsing (`"A-12-07"`) with no string allocations
- [ ] Bounded `Channel<T>` per SignalR connection (DropOldest)
- [ ] `IAsyncEnumerable` + `AsNoTracking` for exports/reports
- [ ] `[LoggerMessage]` source-generated logging on hot paths

### `ArrayPool` rules

- [ ] Rented array may be larger than requested — slice to the requested length
- [ ] Return in `finally`
- [ ] Never use after returning
- [ ] Clear if it held sensitive data

### Hidden allocations to avoid on hot paths

- [ ] Closures capturing variables in lambdas
- [ ] LINQ
- [ ] Boxing in logging
- [ ] String concatenation for Redis keys
- [ ] Objects ≥ 85 KB (Large Object Heap) — pool large buffers

### Tools

- BenchmarkDotNet with `[MemoryDiagnoser]` (Release build only)
- `dotnet-counters`: GC heap size, gen 2 count, thread-pool queue length — during k6 load
- `dotnet-gcdump`: leak hunting

### Acceptance criteria

- [ ] Hold request < 200 B allocated per op (BenchmarkDotNet)
- [ ] `docs/memory.md` has before/after tables and counter charts under load

---

## 11. Identity (Keycloak)

- [ ] Tokens validated at the gateway and in each service (`AddJwtBearer`)
- [ ] Issuer mismatch fixed (browser: `localhost:8080`, containers: `keycloak:8080`) via Keycloak hostname settings or explicit `MetadataAddress` + `ValidIssuer`
- [ ] Authorization policies (e.g. `admin` for event management), not scattered role strings

---

## 12. Observability and health

- [ ] OpenTelemetry: own `ActivitySource` and `Meter` per service
- [ ] Custom metrics: `seats_held`, `seats_sold`, `queue_length`, `oversell_detected` (must stay 0)
- [ ] One logging pipeline (Serilog **or** OTel logs), structured only
- [ ] Liveness = process alive only (never checks dependencies)
- [ ] Readiness checks PostgreSQL, Redis, RabbitMQ (`AspNetCore.HealthChecks.*`)
- [ ] Graceful shutdown:
  - [ ] `HostOptions.ShutdownTimeout` < `terminationGracePeriodSeconds`
  - [ ] `preStop` sleep so the load balancer stops routing first
  - [ ] SignalR connections drained

### Acceptance criteria

- [ ] One trace visible end-to-end: Gateway → Ordering → broker → Payment

---

## 13. Configuration and Aspire

- [ ] Strongly typed options with `ValidateDataAnnotations().ValidateOnStart()`
- [ ] Aspire AppHost used for local development only
- [ ] Hand-written (or reviewed generated) Helm chart for Kubernetes
- [ ] Grafana/Prometheus/Tempo in compose — not only the Aspire dashboard
- [ ] Secrets outside `appsettings` (Vault / External Secrets Operator)

---

## 14. Testing

| Layer | Tools |
|---|---|
| Domain | xUnit, plain unit tests |
| API / integration | `WebApplicationFactory` + Testcontainers (PostgreSQL, Redis, RabbitMQ) + Respawn |
| Concurrency | Real PostgreSQL; `Parallel.ForEachAsync` with 1,000 buyers |
| Saga | MassTransit test harness + `FakeTimeProvider` |
| Architecture | NetArchTest (e.g. Domain does not reference EF Core) |
| Load | k6 or NBomber against compose / kind |
| Chaos | Script kills Payment / Redis mid-sale; invariants still hold |

- [ ] All of the above run in GitHub Actions (load test may be manual/nightly)

---

## 15. Interview talking points

Be able to explain **why**, not only how:

- [ ] Optimistic concurrency, and why the unique constraint is the final safeguard
- [ ] At-least-once delivery, idempotency, outbox — "why not just publish after `SaveChanges`?"
- [ ] Orchestrated vs. choreographed saga
- [ ] Which state is per instance and how it was shared
- [ ] GC behavior in containers and what the benchmarks showed
- [ ] Why the project started as a modular monolith

---

## 16. Learning order

| Phase | Topics |
|---|---|
| 1 | EF Core concurrency, transactions, Testcontainers |
| 2 | Messaging, outbox, saga, resilience, Aspire |
| 3 | Redis, SignalR scale-out, Data Protection, Kubernetes basics, k6 |
| 4 | Span/pooling, BenchmarkDotNet, diagnostic tools, Native AOT |