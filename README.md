# TicketRush — flash-sale seat booking (.NET 10)

> **Status: Phase 1 starter — not a finished microservices platform.** This scaffold was drafted from the GitHub Copilot/Claude Sonnet TicketRush proposal. It has NOT been compiled or tested in the generating environment (no .NET SDK or Docker engine was available there). Build and Testcontainers tests must be run locally before describing any behavior as verified.

A booking system that evolves from a **modular monolith** into distributed, horizontally scalable services. Its core invariant is: **a seat is never sold twice**. PostgreSQL is the source of truth; later Redis caching/admission does not change this.

## Why a separate project from ASP.NET_MVC_Chat_Test?

The existing chat application demonstrates asynchronous pipelines, SSE and bounded `Channel<T>` queues. TicketRush targets business workflows, database concurrency, failure recovery, idempotency, sagas, autoscaling and low-allocation inventory processing. Keep the chat repo intact; this is a new repository.

## Phase 1: implemented starter scope

- One ASP.NET Core Minimal API with initial domain concerns **Catalog, Inventory (holds), Ordering (checkout)**. They share infrastructure in this first scaffold; enforce module ownership and split application services before calling it a mature modular monolith.
- PostgreSQL transaction and deterministic `FOR UPDATE` seat-row locks; an event/seat has one row and one status.
- Atomic holds for 1–6 seats, with expiry timestamps. Expired holds can be reclaimed.
- Idempotency keys for holds and checkout, backed by database unique indexes.
- Confirmed checkout sets the held seats to **Sold** in the same transaction that creates its order (no payment integration yet).
- EF Core 10 context, Npgsql `xmin` row-version mapping, containerized API and PostgreSQL schema bootstrap.
- xUnit + Testcontainers test source for 1,000 booking attempts on 100 seats and idempotency.

**Important limitations:** authentication is not implemented, the buyer ID is supplied by the caller, checkout is not a payment, there is no waiting room, and Redis/RabbitMQ/sagas/Kubernetes/Aspire/Native AOT are **planned**, not implemented. This local API must not be exposed publicly.

## Quick start (Docker)

Requires Docker Desktop or Docker Engine. For IDE development with .NET 10, use **Visual Studio 2026 (18.0+)** rather than Visual Studio 2022, or use the .NET 10 command-line SDK with VS Code. From repo root:

```bash
docker compose up --build
```

API: `http://localhost:8088` ; PostgreSQL: localhost port `5439`.

The development database uses `deploy/postgres/001_schema.sql` **only when the Postgres volume is created for the first time**. Use `docker compose down -v` to wipe the demo DB and reinitialize it (destroys all data).

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

Tests create ephemeral PostgreSQL instances via Testcontainers; Docker must be running. The test code is included but was **not executed** in the generating environment. Fix any restore/build failures before making repository claims.

## Why row locks + a single authoritative seat row?

- A unique `(event_id, seat_code)` key means there is exactly one authoritative allocation row for a seat.
- `SELECT ... FOR UPDATE`, inside a DB transaction, serializes contenders for the **same** seat, even across API replicas.
- Reclaiming expired holds is safe because checkout checks ownership/expiry inside the same transaction while locking the hold and seats.
- `xmin` is an additional optimistic-concurrency safety measure; uniqueness by itself does **not** stop two processes from updating a single row in sequence, so row-level state transitions matter.
- Lock waits and hot events can constrain performance. This is intentional correctness-first design; measure before replacing it.

## Roadmap

See `docs/architecture.md`, `docs/roadmap/README.md`, and `docs/implementation-notes.md` (supplied by the Copilot proposal). Phase 2 introduces independent Inventory, Ordering and Payment services and a MassTransit saga; Phase 3 adds Redis admission control, live seat maps, Kubernetes, and scale tests; Phase 4 measures heap allocations, bitset seat-map representation and AOT where useful.

## Next code change

Replace one-off development bootstrap SQL with **EF Core migrations** and a dedicated migration job, and expand the concurrency tests (multi-seat racing orders, hold expiry, competing checkouts, transaction failure/rollback). Do not expose public booking endpoints until Keycloak and authorization are in place.

## EF model-derived SQL identifiers

Raw PostgreSQL locking queries now use `IEntitySchema<TEntity>` backed by the finalized EF Core model instead of repeating table/column names. See `docs/entity-schema-refactor.md`.


## Phase 1 modularization

Catalog event queries are now implemented in `TicketRush.Catalog`. See [docs/catalog-extraction.md](docs/catalog-extraction.md). Booking operations and the EF migration history remain unchanged.

## Phase-1 module boundaries

- **Catalog:** `TicketRush.Catalog` owns the event-listing query and `IEventCatalog`.
- **Inventory:** `TicketRush.Inventory` owns seat availability and `HoldAsync` (including locking, expiry and hold idempotency). `IInventoryService` is the public in-process boundary.
- **Checkout (temporary):** `BookingService.ConfirmAsync` remains in shared Infrastructure until the Ordering extraction. Its original single PostgreSQL transaction is unchanged.
- `TicketRush.Infrastructure` owns the shared `BookingDbContext`, EF mappings and migrations. No module-specific migration was introduced for these code-only refactorings.

The two module assemblies still reference Infrastructure in Phase 1. They are *not* independent services or independent databases.
