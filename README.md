# TicketRush — flash-sale seat booking (.NET 10)

> **Status: Phase 1 modular monolith, final validation in progress — not a microservices platform.** Catalog, Inventory, and Ordering are extracted; the developer reported a successful build and **24 passing tests**. This follow-up adds two expiry-regression tests and usable hold/order read URLs; run the full suite and Compose smoke locally before claiming Phase 1 completion.

A booking system that evolves from a **modular monolith** into distributed, horizontally scalable services. Its core invariant is: **a seat is never sold twice**. PostgreSQL is the source of truth; later Redis caching/admission does not change this.

## Why a separate project from ASP.NET_MVC_Chat_Test?

The existing chat application demonstrates asynchronous pipelines, SSE and bounded `Channel<T>` queues. TicketRush targets business workflows, database concurrency, failure recovery, idempotency, sagas, autoscaling and low-allocation inventory processing. Keep the chat repo intact; this is a new repository.

## Phase 1: implemented starter scope

- One ASP.NET Core Minimal API with **Catalog**, **Inventory**, and **Ordering** assemblies. Ordering coordinates checkout via Inventory's transactional checkout interface. All modules share EF Core persistence and one PostgreSQL database.
- PostgreSQL transaction and deterministic `FOR UPDATE` seat-row locks; an event/seat has one row and one status.
- Atomic holds for 1–6 seats, with expiry timestamps. Expired holds can be reclaimed.
- Idempotency keys for holds and checkout, backed by database unique indexes.
- Confirmed checkout sets the held seats to **Sold** in the same transaction that creates its order (no payment integration yet).
- EF Core 10 context, Npgsql `xmin` row-version mapping, generated `InitialCreate` migration, dedicated `TicketRush.DbMigrator` job and containerized API.
- xUnit + Testcontainers tests cover booking concurrency, hold expiry, idempotency, migrations, Catalog queries, and Inventory seat projection. The developer confirmed **24 tests pass** before this completion patch. This patch adds two expiry-regression tests (**26 expected**), pending local execution.

**Important limitations:** authentication is not implemented, the buyer ID is supplied by the caller, checkout is not a payment, there is no waiting room, and Redis/RabbitMQ/sagas/Kubernetes/Aspire/Native AOT are **planned**, not implemented. This local API must not be exposed publicly.

## Quick start (Docker)

Requires Docker Desktop or Docker Engine. For IDE development with .NET 10, use **Visual Studio 2026 (18.0+)** rather than Visual Studio 2022, or use the .NET 10 command-line SDK with VS Code. From repo root:

```bash
docker compose up --build
```

API: `http://localhost:8088` ; PostgreSQL: localhost port `5439`. Both ports are bound to **127.0.0.1 only** by Compose because the API has no authentication and the database credentials are development-only. Do not forward these ports or expose this stack on a public network.

Docker Compose starts PostgreSQL, runs the `migrate` service to apply EF Core migrations and optionally seed demo data (`SEED_DEMO_DATA=true`), and only then starts the API. `deploy/postgres/001_schema.sql` is legacy material and is **not executed by Compose**. To intentionally reset disposable local data, `docker compose down -v` removes the PostgreSQL volume (all database data is destroyed).

### Try the API

```bash
curl http://localhost:8088/events
curl http://localhost:8088/events/11111111-1111-1111-1111-111111111111/seats

curl -X POST http://localhost:8088/events/11111111-1111-1111-1111-111111111111/holds \
  -H 'Content-Type: application/json' \
  -d '{"buyerId":"22222222-2222-2222-2222-222222222222", "idempotencyKey":"hold-demo-1", "seatCodes":["A-001","A-002"]}'
```

Copy the `id` from the hold response into the `holdId` field below:

```bash
curl -X POST http://localhost:8088/orders -H 'Content-Type: application/json' \
  -d '{"buyerId":"22222222-2222-2222-2222-222222222222", "idempotencyKey":"order-demo-1", "holdId":"<HOLD-ID>"}'
```

Send the same idempotency key again: expect the same hold/order. A different buyer attempting an already-held seat should get HTTP 409. Simulate an expired hold by waiting 10 minutes; expired **unsold** seats may be reclaimed, but orders never are.

### Run tests

```bash
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx
dotnet test TicketRush.slnx
```

Tests create ephemeral PostgreSQL instances via Testcontainers; Docker must be running. The developer reported a successful build and all **24** tests passing. This patch adds two expiry tests (26 expected) using a controlled `TimeProvider` and PostgreSQL Testcontainers; run them locally (the patch author's environment has no .NET SDK or Docker).

## Why row locks + a single authoritative seat row?

- A unique `(event_id, seat_code)` key means there is exactly one authoritative allocation row for a seat.
- `SELECT ... FOR UPDATE`, inside a DB transaction, serializes contenders for the **same** seat, even across API replicas.
- Reclaiming expired holds is safe because checkout checks ownership/expiry inside the same transaction while locking the hold and seats.
- `xmin` is an additional optimistic-concurrency safety measure; uniqueness by itself does **not** stop two processes from updating a single row in sequence, so row-level state transitions matter.
- Lock waits and hot events can constrain performance. This is intentional correctness-first design; measure before replacing it.

## Roadmap

See `docs/architecture.md`, `docs/roadmap/README.md`, and `docs/implementation-notes.md` (supplied by the Copilot proposal). Phase 2 introduces independent Inventory, Ordering and Payment services and a MassTransit saga; Phase 3 adds Redis admission control, live seat maps, Kubernetes, and scale tests; Phase 4 measures heap allocations, bitset seat-map representation and AOT where useful.

## Phase 1 exit validation

Run `dotnet test TicketRush.slnx` (26 tests expected with this patch), `docker compose up -d --build`, and `powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1`. For the acceptance checklist and accurate 1,000-attempt concurrency description, see [docs/phase1-completion.md](docs/phase1-completion.md).

Known remaining limitations: no authenticated buyer identity, no payment, no measured multi-replica scale or memory benchmarks. This API is a **local demonstration only**, not appropriate for public deployment. Before Phase 2, capture the test result and add a brief Phase 1 architecture/limitations summary. Avoid splitting the shared checkout transaction until distributed saga/outbox design is ready.

## EF model-derived SQL identifiers

Raw PostgreSQL locking queries now use `IEntitySchema<TEntity>` backed by the finalized EF Core model instead of repeating table/column names. See `docs/entity-schema-refactor.md`.


## Phase 1 modularization

Catalog event queries are now implemented in `TicketRush.Catalog`. See [docs/catalog-extraction.md](docs/catalog-extraction.md). The EF migration history remains unchanged; booking invariants are preserved through the service extraction.

## Phase-1 module boundaries

- **Catalog:** `TicketRush.Catalog` owns the event-listing query and `IEventCatalog`.
- **Inventory:** `TicketRush.Inventory` owns seat availability and `HoldAsync` (including locking, expiry and hold idempotency). `IInventoryService` is the public in-process boundary.
- **Ordering:** `TicketRush.Ordering` owns checkout idempotency and `TicketOrder` creation. It calls the Inventory checkout port, which locks and updates holds/seats within the same PostgreSQL transaction.
- `TicketRush.Infrastructure` owns the shared `BookingDbContext`, EF mappings and migrations. No module-specific migration was introduced for these code-only refactorings.

The three module assemblies still reference Infrastructure in Phase 1. They are *not* independent services or independent databases.


See `docs/ordering-extraction.md` for the Phase 1 Ordering boundary and validation steps.
