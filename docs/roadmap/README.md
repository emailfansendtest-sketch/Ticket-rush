# Implementation phases and exit criteria

## Phase 1 — modular monolith (26 tests reported passing; Docker smoke not yet confirmed)

Catalog, Inventory and Ordering are now separate assemblies in one ASP.NET Core host. PostgreSQL remains authoritative, with one shared EF Core DbContext and atomic checkout transaction. **Not yet independent microservices.**

- [x] Solution, working API, PostgreSQL migrations, Docker migration job
- [x] Catalog and Inventory extracted from API / Infrastructure
- [x] Ordering owns checkout idempotency; Inventory owns seat transitions
- [x] Multi-seat concurrency, expiry/reclaim, and checkout rollback tests reported passing
- [x] Developer verified build and 26 integration tests on October 20, 2026, including expiry/HTTP fixes
- [ ] Run Compose smoke and document the final Phase 1 test evidence (docs/phase1-completion.md)
- [ ] Optional local-demo hardening: bounded HTTP payloads, structured logging, readiness endpoint

**Exit:** the HTTP workflow and existing real-PostgreSQL concurrency suite pass without double-sold seats. Do not represent this as proof of production-scale load, independent deployment, payment reliability, or authentication.

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
