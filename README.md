# TicketRush — flash-sale seat booking (.NET 10)

> **Status: Phase 2 Step 2.3a Ordering durable-intake foundation (local validation pending).** The validated Step 2.2 baseline has 32 passing tests and a passing Docker Compose smoke test. This increment adds an independent Ordering database and atomic Pending-order/outbox writes; the existing Booking HTTP checkout still uses the Phase 1 synchronous transaction.

A booking system that evolves from a **modular monolith** into distributed, horizontally scalable services. Its core invariant is: **a seat is never sold twice**. PostgreSQL is the source of truth; later Redis caching/admission does not change this.

## Why a separate project from ASP.NET_MVC_Chat_Test?

The existing chat application demonstrates asynchronous pipelines, SSE and bounded `Channel<T>` queues. TicketRush targets business workflows, database concurrency, failure recovery, idempotency, sagas, autoscaling and low-allocation inventory processing. Keep the chat repo intact; this is a new repository.

## Phase 1: implemented starter scope

- The Phase 1 application started as one ASP.NET Core Minimal API with **Catalog**, **Inventory**, and **Ordering** assemblies. Ordering coordinates checkout via Inventory's transactional checkout interface. After this step, Catalog has its own database and API; Inventory and Ordering continue sharing the original booking database/transaction.
- PostgreSQL transaction and deterministic `FOR UPDATE` seat-row locks; an event/seat has one row and one status.
- Atomic holds for 1–6 seats, with expiry timestamps. Expired holds can be reclaimed.
- Idempotency keys for holds and checkout, backed by database unique indexes.
- Confirmed checkout sets the held seats to **Sold** in the same transaction that creates its order (no payment integration yet).
- EF Core 10 context, Npgsql `xmin` row-version mapping, generated `InitialCreate` migration, dedicated `TicketRush.DbMigrator` job and containerized API.
- xUnit + Testcontainers tests cover booking concurrency, hold expiry, idempotency, migrations, Catalog queries, and Inventory seat projection. The developer confirmed **32 tests after the reviewed Catalog extraction**. Step 2.3a adds eight Ordering-intake test cases (40 expected; local validation pending).

**Important limitations:** authentication is not implemented, the buyer ID is supplied by the caller, checkout is not a payment, there is no waiting room, and Redis/RabbitMQ/sagas/Kubernetes/Aspire/Native AOT are **planned**, not implemented. This local API must not be exposed publicly.

## Quick start (Docker)

Requires Docker Desktop or Docker Engine. For IDE development with .NET 10, use **Visual Studio 2026 (18.0+)** rather than Visual Studio 2022, or use the .NET 10 command-line SDK with VS Code. From repo root:

```bash
docker compose up --build
```

Booking API: `http://localhost:8088`; Catalog API: `http://localhost:8090`; Booking PostgreSQL: localhost port `5439`; Catalog PostgreSQL: localhost port `5440`; Ordering PostgreSQL: localhost port `5441`. All published ports bind to **127.0.0.1 only** by Compose because the API has no authentication and the database credentials are development-only. Do not forward these ports or expose this stack on a public network.

Compose starts Booking, Catalog and Ordering PostgreSQL containers, runs separate `migrate`, `catalog-migrate`, and `ordering-migrate` jobs, and then starts the Booking and Catalog APIs. The new Ordering database is only used by tests and its migrator in this increment; no Ordering HTTP service or outbox publisher exists yet. The Booking and Catalog migration jobs seed the same fixed demo event ID for their temporary independent roles; Ordering migration creates schema without seeding orders. The old Booking database retains its local event-reference table to satisfy Inventory foreign keys during the transition. `deploy/postgres/001_schema.sql` is legacy material and is **not executed by Compose**. To intentionally reset disposable local data, `docker compose down -v` removes the PostgreSQL volume (all database data is destroyed).

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

Tests create ephemeral PostgreSQL instances via Testcontainers; Docker must be running. The developer reported 32 tests passing before this patch. This adds eight Ordering intake test cases, for 40 expected. Tests require Docker/Testcontainers; the patch author could not run .NET or Docker here.

## Why row locks + a single authoritative seat row?

- A unique `(event_id, seat_code)` key means there is exactly one authoritative allocation row for a seat.
- `SELECT ... FOR UPDATE`, inside a DB transaction, serializes contenders for the **same** seat, even across API replicas.
- Reclaiming expired holds is safe because checkout checks ownership/expiry inside the same transaction while locking the hold and seats.
- `xmin` is an additional optimistic-concurrency safety measure; uniqueness by itself does **not** stop two processes from updating a single row in sequence, so row-level state transitions matter.
- Lock waits and hot events can constrain performance. This is intentional correctness-first design; measure before replacing it.

## Roadmap

See `docs/architecture.md`, `docs/roadmap/README.md`, and `docs/implementation-notes.md` (supplied by the Copilot proposal). Phase 2 is extracting Catalog first, then independent Inventory and Ordering with RabbitMQ/outbox and a saga; Phase 3 adds Redis admission control, live seat maps, Kubernetes, and scale tests; Phase 4 measures heap allocations, bitset seat-map representation and AOT where useful.

## Phase 1 exit validation

Run `dotnet test TicketRush.slnx` (40 tests expected for Phase 2 Step 2.3a, subject to local verification), `docker compose up -d --build`, and `powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1`. For the acceptance checklist and accurate 1,000-attempt concurrency description, see [docs/phase1-completion.md](docs/phase1-completion.md).

Known remaining limitations: no authenticated buyer identity, no payment, no measured multi-replica scale or memory benchmarks. This API is a **local demonstration only**, not appropriate for public deployment. Before Phase 2, capture the test result and add a brief Phase 1 architecture/limitations summary. Avoid splitting the shared checkout transaction until distributed saga/outbox design is ready.

## EF model-derived SQL identifiers

Raw PostgreSQL locking queries now use `IEntitySchema<TEntity>` backed by the finalized EF Core model instead of repeating table/column names. See `docs/entity-schema-refactor.md`.


## Phase 1 modularization

Catalog event queries are now implemented in `TicketRush.Catalog.Storage` behind `IEventCatalog`; the Booking API uses `HttpEventCatalog`. See [docs/catalog-extraction.md](docs/catalog-extraction.md). The EF migration history remains unchanged; booking invariants are preserved through the service extraction.

## Phase-1 module boundaries

- **Catalog:** the Catalog API and Storage project own event discovery and persistence; `TicketRush.Catalog` exposes its read contract/client.
- **Inventory:** `TicketRush.Inventory` owns seat availability and `HoldAsync` (including locking, expiry and hold idempotency). `IInventoryService` is the public in-process boundary.
- **Ordering:** `TicketRush.Ordering` owns checkout idempotency and `TicketOrder` creation. It calls the Inventory checkout port, which locks and updates holds/seats within the same PostgreSQL transaction.
- `TicketRush.Infrastructure` owns the shared `BookingDbContext`, EF mappings and migrations. No module-specific migration was introduced for these code-only refactorings.

The three module assemblies still reference Infrastructure in Phase 1. They are *not* independent services or independent databases.


See `docs/ordering-extraction.md` for the Phase 1 Ordering boundary and validation steps.

## Phase 2 Step 2.2: independently deployed Catalog (local preview)

See `docs/phase2-catalog-extraction.md` and ADR 0008. The existing Booking API
`GET /events` now calls the Catalog API over HTTP. Inventory/Ordering still use
local Booking PostgreSQL transactions and the booking event-reference table;
**do not claim Inventory is yet a standalone microservice.**

```powershell
Invoke-RestMethod http://localhost:8090/events
Invoke-RestMethod http://localhost:8088/events
```

Catalog has its own storage context and initial EF migration; the two databases
can be migrated, deployed and recovered independently. No authentication, event
provisioning or cross-service consistency mechanism is included in this step.

Catalog forwarding behavior: when Catalog is unavailable, Booking `GET /events` returns HTTP 503; a Catalog request timeout returns HTTP 504 (10-second client timeout). The endpoint does not fall back to stale Booking database event data.

### Phase 2.3a — Ordering durable intake foundation

A separate `TicketRush.Ordering.Persistence` project and `ordering-postgres` database now store Pending checkout intents and `CheckoutRequestedV1` outbox messages atomically. This path is not wired to the existing synchronous `/orders` endpoint yet. See [phase2-ordering-intake](docs/phase2-ordering-intake.md).
