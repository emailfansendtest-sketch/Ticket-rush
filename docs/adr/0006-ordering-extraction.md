# ADR 0006: Ordering module extraction within the modular monolith

## Status
Accepted for Phase 1 (local validation required)

## Context
`BookingService` combined inventory row locking and checkout/order idempotency.
Inventory already owns hold creation and availability. We need a meaningful
Ordering boundary without introducing distributed transactions prematurely.

## Decision
- Ordering owns `IOrderingService.ConfirmAsync`, the order's unique key,
  replay/conflict responses, and insertion of `TicketOrder`.
- Inventory owns `IInventoryCheckout.PrepareCheckoutAsync`: lock the hold,
  validate buyer and hold status; lock associated seats; validate expiry;
  and update tracked seats/hold to Sold/Confirmed.
- Both services share ONE request-scoped `BookingDbContext`.
- Ordering begins/commits the ONE PostgreSQL transaction and invokes
  `SaveChangesAsync()` once for order + inventory changes. Inventory never
  starts/commits/saves in `PrepareCheckoutAsync`.
- Lock order is preserved: hold row first, then seats ordered by seat code.
  Expiration is checked after acquiring both locks.
- No tables, EF entity configurations, or migrations are modified.

## Trade-offs and next steps
- This is an in-process boundary, NOT an independently deployable service.
  `IInventoryCheckout` exposes an internal transactional protocol with one
  shared DB context; it must not be used across remote service boundaries.
- A future independent Ordering service requires an outbox, inbox/idempotency,
  and saga for Inventory coordination (Phase 2), not an attempt to share a
  database transaction across service processes.
- Phase-1 authentication, read/write API tests, architecture boundary rules,
  and error-path regression testing may be developed separately.

## Correctness note

The CreatedAtUtc timestamp is normalized to the 1-microsecond precision of PostgreSQL
`timestamptz` before both persistence and the initial Created response. This
keeps the complete OrderView identical on idempotent replay. A concurrency
integration test verifies that a duplicate order idempotency key cannot leave
partially-sold seats or confirmed holds in the losing transaction.
