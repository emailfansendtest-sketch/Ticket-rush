# ADR 0002 — PostgreSQL owns seat allocation

**Status:** Accepted.

## Decision

Keep one authoritative `(event_id, seat_code)` allocation row per seat, with status `Available`, `Held`, `Sold`. Multi-seat holds acquire PostgreSQL row locks in stable order and change all selected rows in one transaction. Checkout validates hold ownership/expiry and changes the seat statuses and order atomically. Redis/Valkey is introduced only in Phase 3 as an accelerator/admission coordinator; absence of Redis hold keys cannot imply sale availability.

## Tradeoffs

- PostgreSQL row locks introduce queueing during flash sales; the waiting room will bound concurrency in later phases.
- Transactions do not create exactly-once distributed processing; later cross-service workflows use idempotency, outbox/inbox, and saga compensation.
- Cache expiry and DB hold expiry are not identical; reconciliation and failure behavior must be tested before enabling Redis holds.
- A sold seat remains Sold even if its old Redis key is absent/expired.
