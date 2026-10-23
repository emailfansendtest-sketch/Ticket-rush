# TicketRush implementation roadmap

This is the **current status summary for the Phase 2.3r source snapshot (23 October 2026)**. Historic root-level `README-STEP*.md` documents and earlier phases' ADRs describe their own implementation dates; they are not reliable as a live project-status summary.

## Phase 1 — transactional booking baseline — completed

- [x] ASP.NET Core/EF Core/PostgreSQL booking domain and migrations.
- [x] Atomic seat holds, expiry/reassignment, idempotency, concurrent checkout, and real-PostgreSQL integration tests.
- [x] Initial Catalog/Inventory/Ordering module boundaries.

## Phase 2 — independently persisted distributed checkout — completed through 2.3r

- [x] Catalog service and PostgreSQL ownership.
- [x] Ordering-owned database, checkout-intake API, and transactional outbox.
- [x] RabbitMQ command/result publishers, inbox deduplication, and persistent saga worker.
- [x] Keycloak JWT identity checks and signed hold-proof admission for distributed checkout.
- [x] Payment simulator, asynchronous authorization gate, authorization-void compensation.
- [x] Simulated Notification worker with deduplicated persistent receipts.
- [x] Inventory HTTP API, independently owned PostgreSQL database, EF migrations and guarded historical cutover tool.
- [x] Normal Booking gateway no longer owns a Booking database connection; legacy synchronous `/orders` returns HTTP 410.
- [x] Local reliability drills: Inventory-consumer outage (2.3p), RabbitMQ outage/outbox recovery (2.3q), repeated terminal checkout fact (2.3r).

**Important limitation:** These are validated local demonstration workflows, not an external payment integration, measured multi-replica deployment, full operational monitoring stack or production readiness proof. The checkout saga is custom RabbitMQ.Client-based code, not MassTransit.

## Phase 3 — traffic/performance and horizontal scale — next

- [ ] Reproducible read-only HTTP baseline, JSON results and container snapshots (planned Phase 3a; **not present in this source snapshot**).
- [ ] Isolated concurrent seat-hold contention baseline and correctness/load reports.
- [ ] Redis/Valkey-based waiting room/admission control with PostgreSQL authority preserved.
- [ ] SignalR seat availability snapshots/updates, reconnect and reconciliation.
- [ ] Docker/Kubernetes multi-replica tests, load shedding, health/readiness, and measured scaling curves.
- [ ] Operational tracing, metrics and recovery runbooks suitable for demonstrating failures and bottlenecks.

## Phase 4 — memory/GC and optional AOT

- [ ] Measure managed allocations, object lifetime, working set, GC events under representative load.
- [ ] Benchmark compact/bitset seat maps, serialization, pooling and incremental updates **against the current baseline**.
- [ ] Assess Native AOT only where compatible, and report its trade-offs.

## Phase 5 — presentation

- [ ] Measured comparison reports, architecture screenshots/demo, and trade-off ADRs.
- [ ] Reproducible CI or documented local validation, with no invented benchmarks.

See the [root README](../../README.md) for correct local setup, the current API, and verification scripts; see [Phase 2 implementation history](../phase2-work-plan.md) for milestone summaries.
