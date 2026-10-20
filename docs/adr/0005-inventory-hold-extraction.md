# ADR 0005 — Inventory owns seat queries and hold creation

**Status:** Proposed; pending local `dotnet build` and Testcontainers validation.

## Decision

Extract the atomic `HoldAsync` reservation flow into `TicketRush.Inventory.InventoryService`. Also move the `GET /events/{eventId}/seats` query behind `IInventoryService.ListSeatsAsync`, and group both existing Inventory API routes in `InventoryEndpoints`.

Retain the exact PostgreSQL `SELECT … FOR UPDATE`, idempotency recheck, `xmin` handling, transaction/exception behavior, and timestamp normalization from the previously tested implementation. `BookingService` becomes checkout-only for now; its `ConfirmAsync` method is unchanged. Tests call `InventoryService` for holds and `BookingService` for checkout.

Dependency direction: `Api → Inventory → Infrastructure → Domain`. `Infrastructure` must not reference Inventory. Catalog continues to have the same shape.

## Non-goals

No new database, migration, broker, API route, payment flow or distributed transaction. No separation of `ConfirmAsync` yet; its atomic hold, seats and order updates remain within a single database transaction.

## Acceptance

All existing concurrency/idempotency tests pass with calls against the new Inventory service, and new seat-listing/architecture tests pass. The HTTP routes, returned status codes, and seat listing JSON shape are preserved.
