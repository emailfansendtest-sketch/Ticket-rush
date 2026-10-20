# ADR 0007: Phase 2 service ownership and checkout protocol

Status: **Proposed for Phase 2 implementation** (not yet deployed)

## Context

The completed Phase 1 application has Catalog, Inventory and Ordering modules in a single ASP.NET Core host and a single `BookingDbContext` transaction. Splitting only the assemblies or HTTP hosts would still leave a distributed monolith with shared persistence. In particular, `OrderingService.ConfirmAsync()` and `IInventoryCheckout.PrepareCheckoutAsync()` rely on one database transaction, which will not span independently owned databases.

## Decision

- **Catalog** owns event metadata. Its catalog storage/migrations belong to Catalog; event identifiers are referenced by Inventory but not joined across service databases.
- **Inventory** owns seat allocations and holds, decides whether a hold has expired, and is the only writer allowed to transition an allocation to Sold. Inventory validates any sale command using PostgreSQL locking/constraints; a message or Payment acknowledgement never counts as permission to bypass this check.
- **Ordering** owns orders, checkout idempotency, and the durable checkout state/saga correlation. Its database stores a stable order ID with states including `Pending`, `Completed`, and `Failed` (not implemented yet). A successful retry with the same buyer/idempotency key returns the same order; key reuse for a different hold conflicts.
- **Payment simulator** owns captured-payment and refund records, with its own idempotency constraints. Its contract requires a decided pricing model and amount/currency; do **not** invent payment amounts in this first step.
- **Notification** consumes completion facts; notification failure must not undo a completed sale.

Services must not reference another service's EF entities, DbContext or database migrations. Cross-service references are identifiers, messages, and narrowly scoped HTTP/transport contracts.

**Catalog extraction migration warning:** the Phase 1 `BookingDbContext` and `InitialCreate` migration contain `FK_holds_events_event_id` and `FK_seats_events_event_id`. Those cannot remain foreign keys into Catalog once its database is separate. Catalog extraction must plan to (a) migrate event metadata and seeds into Catalog-owned storage, (b) remove the cross-owner foreign-key constraints through a deliberate Inventory-side migration, (c) keep `EventId` as an external identifier in Inventory, and (d) define how Inventory learns about newly created/disabled events. Do not introduce a synchronous Catalog call while holding Inventory seat-row locks; preserve local PostgreSQL as the decision-maker for seat allocation.

## Checkout: preliminary distributed protocol

1. Ordering transaction: persist a `Pending` order and `CheckoutRequestedV1` in its transactional outbox; return `202 Accepted` with an order status URL. Nothing has been sold yet.
2. A durable orchestrator records an ordered, idempotent workflow. Payment simulator captures (or fails) payment based on a real, explicitly defined amount/currency. All commands carry a stable order ID and deduplication identity.
3. After payment capture is durably recorded, the orchestrator requests Inventory's sale of the existing hold. Inventory atomically checks buyer, hold expiry, seat ownership, and prior completion before committing allocations, then emits `InventorySaleCommittedV1` or `InventorySaleRejectedV1` through its own outbox.
4. If Inventory rejects/hold expires after capture, orchestrator requests an idempotent refund. The saga does not enter the final Failed state until compensation reaches a terminal result (or remains visibly in a retryable compensation state).
5. After Inventory success, Ordering marks the order `Completed`; Notification is a subsequent independent consumer.

Wire-format requirements must be agreed before production publication: serializer naming/options, required-property enforcement, accepted reason codes, semantic validation of nonempty identifiers and UTC timestamps, and schema-evolution/compatibility tests. The initial round-trip tests do not guarantee broker-envelope compatibility or reject malformed business identifiers. The precise payment and saga command contracts will be versioned after the pricing and refund behavior is implemented. The four initial message records in `TicketRush.BuildingBlocks.Contracts` describe only the stable checkout/Inventory facts and are **not yet published by application code**.

## Invariants and failure handling

- **Never oversell:** Inventory's PostgreSQL records/constraints decide who owns each seat; at most one completed order may own a seat.
- **No lost intent:** Persist each outgoing message in the same local transaction as its state change (transactional outbox); publish after commit via a separate delivery worker. Do not publish immediately before `SaveChangesAsync`.
- **At least once, not exactly once:** broker redeliveries and dispatcher retries are expected. Keep the original `MessageId` stable on redelivery, deduplicate inbox processing, and enforce unique business operation keys (`OrderId`, `HoldId`, buyer/idempotency key) within each owner database.
- **Out-of-order replies:** correlate via `OrderId` and persist progress; do not assume that broker ordering across consumers is reliable.
- **Timeout:** a hold may expire while Payment is delayed. Check expiry when Inventory acquires its row locks; compensate payment rather than selling an expired hold.
- **Crash points:** test before/after Order+outbox commit, before/after Payment write+outbox, before/after Inventory write+outbox, and before/after broker ack. Restarting services must not yield duplicate charges or sales.
- **Status consistency:** Phase 1 `201 Created` synchronous checkout changes to `202 Accepted` pending checkout in Phase 2 intentionally; API contracts and tests must be updated with this change when the saga becomes operational.

## Transition strategy

Do not change Phase 1 service runtime code until service-owned storage and the outbox/consumer flows exist. First add transport-neutral contracts, then extract a read-only Catalog host, then split Inventory and Ordering persistence deliberately. The 26 passing Phase 1 integration tests remain regression evidence for the original synchronous transactional flow; new Phase 2 saga tests supplement rather than blindly replace them.

## Open design items

- Pricing/currency/tax ownership and payment-command schema
- Whether reservations should be lengthened while payment is in progress (without guaranteeing a sale past expiry)
- Persisted saga state model and concurrency token
- Status/read API authentication and service-to-service authentication
- Broker failure policy, poison-message replay runbook and retry/dead-letter limits
