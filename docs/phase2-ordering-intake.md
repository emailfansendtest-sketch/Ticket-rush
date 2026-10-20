# Phase 2.3a — Ordering-owned durable checkout intake

## Scope

This increment adds independent **Ordering-owned PostgreSQL persistence** and the
first **transactional outbox write**, without changing the working synchronous
Booking API or its 32 passing regression tests. It is an **additive foundation**,
not yet a running distributed checkout system.

- `TicketRush.Ordering.Persistence` owns `OrderingDbContext`, `OrderIntent`,
  `OutboxMessage`, and `CheckoutIntakeService`.
- `TicketRush.Ordering.DbMigrator` applies the Ordering-only EF Core migration.
- `ordering-postgres` is separate from Catalog and the existing Booking DB.
- `CheckoutIntakeService.AcceptAsync` creates exactly one Pending intent and one
  `CheckoutRequestedV1` outbox row **in the same database transaction**.
- Idempotency is enforced by PostgreSQL unique constraints on
  `(buyer_id, idempotency_key)` **and** `hold_id`, not an in-memory cache.
- The same buyer/key/hold returns a replay of the existing order ID; same key
  with another hold and same hold with another key conflict. Different buyers
  may reuse the same key for different holds.
- The outbox retains a stable `MessageId` and `PayloadJson` until it is published.
  `PublishedAtUtc` must not be set until a future dispatcher receives publisher
  confirmation. An at-least-once dispatcher and inbox deduplication come next.

## Important behavior boundaries

**No seat is sold in this increment.** `Accepted` means Ordering has durably
recorded a Pending checkout, not that Inventory accepted it, nor that payment
was captured. In fact, Ordering cannot verify that `HoldId` exists: only Inventory
can validate it later. The current `/orders` endpoint intentionally retains its
Phase 1 synchronous semantics and its original Booking DB. Do not wire this
new intake service into `/orders` without moving to a `202 Accepted` API and
building the corresponding saga, status endpoint, inventory command handler,
and compensation logic.

There is no RabbitMQ publisher, consumer, Payment service, saga, or distributed
checkout path yet. We deliberately avoid pretending that persisting a message
is equivalent to delivering or handling it.

## Local verification

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

docker compose up -d --build
docker compose ps
docker compose logs ordering-migrate
```

Expected: the new `ordering-migrate` job completes with exit code 0. The old
Booking API and Catalog service should continue to work unchanged, including:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
```

New integration tests exercise Ordering migrations, pending-intent/outbox
atomicity (including rollback on injected outbox-write failure), replay and conflict semantics, concurrent requests, and the absence
of a dependency on the old Booking persistence layer. Because this is an
additive step, the old 32 tests should remain intact; the seven new test methods
include one theory with two cases (expected total **40** test cases).

## Next implementation steps

1. Introduce a persistent outbox dispatcher to RabbitMQ with publish confirms,
   bounded retries, and a stable `MessageId` on redelivery; write broker-backed tests.
2. Extract Inventory-owned database and command consumer. Define independent
   Inventory sale idempotency, including redeliveries and rejection reasons.
3. Add durable saga state and Payment simulator. Only then switch the checkout
   HTTP entry point to an asynchronous `202 Accepted` workflow and remove the
   synchronous shared-transaction implementation.

Migration files were authored as part of this patch. Verify the EF model and
migration snapshot against your local SDK (`dotnet ef migrations has-pending-model-changes`
with the new context) before relying on the migration in non-disposable databases.
