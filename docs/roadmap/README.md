# Implementation phases and exit criteria

## Phase 1 — modular monolith (first priority)

Catalog, Inventory, Ordering share one host but enforce logical module boundaries. PostgreSQL authoritative. Hold/confirm/reclaim code and real-Postgres race tests.

- [x] Draft project structure, basic endpoints, schema, Docker Compose and concurrency test source **(unverified: no build runtime available)**
- [ ] Run restore/build/tests on local SDK and fix any defects
- [ ] Complete EF migrations and migration job (replace bootstrap DDL)
- [ ] Add concurrent multi-seat and expiry/reclaim tests
- [ ] Define ownership and document payment-less demo checkout semantics
- [ ] Add bounded request validation, error codes and structured logging

**Exit:** demonstrated **0 double-sold seats** under a reproducible real-PostgreSQL contention test; no claims before this passes.

## Phase 2 — extract microservices

- [ ] Extract Inventory service/database owner, Catalog read API and Ordering service
- [ ] Add RabbitMQ (MassTransit v8 licensing verified before use) and transactional outbox/inbox
- [ ] Orchestrated checkout saga with fake Payment adapter and compensation
- [ ] Add Keycloak; move BuyerId from caller to authenticated identity
- [ ] Aspire AppHost + OpenTelemetry + Testcontainers for broker and DB

**Exit:** force-killing Payment mid-checkout loses no completed orders; saga resumes or compensates idempotently.

## Phase 3 — scale-out

- [ ] Redis/Valkey admission/waiting-room; fail-closed admission policy
- [ ] Redis holds only as acceleration, with PostgreSQL as authority and reconciliation
- [ ] SignalR seat-map updates and reliable reconnect/snapshot behavior
- [ ] 3+ replica deployments with Helm; shared keys, gateway-global rate limits, autoscaling
- [ ] k6/NBomber reproducible load tests and failures (Redis, broker, pod)

**Exit:** zero oversell under simultaneous load; published scale, throughput and p95/p99 charts.

## Phase 4 — memory and AOT

- [ ] Compact bitset seat map with measured allocations
- [ ] Benchmarks for parsers/serializers/pools and GC under container limits
- [ ] Inventory Native AOT only if its dependencies permit; compare against JIT
- [ ] Produce `docs/memory.md` with actual measurements

**Exit:** reproducible before/after memory/latency results, not hand-picked theoretical numbers.

## Phase 5 — presentation

- [ ] README hero architecture, short GIF, CI badges
- [ ] ADRs for key tradeoffs, chaos runbook, failure evidence
- [ ] Explicit honest scope, limitations and measured benchmarks
