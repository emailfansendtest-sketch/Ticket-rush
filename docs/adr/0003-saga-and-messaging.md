# ADR 0003 — Future checkout orchestration

**Status:** Proposed for Phase 2, NOT implemented in the starter.

Use an orchestrated MassTransit saga for order state. Ordering owns the saga/outbox; Inventory owns seat transitions; Payment is a fake external PSP boundary with a durable payment-attempt idempotency key. Notifications are asynchronous after durable order completion.

Proposed flow: OrderRequested -> Inventory.ConfirmHold/ReserveForPayment -> Payment.Authorize -> Inventory.FinalizeSale -> OrderCompleted. On rejection/timeout compensate by releasing the still-owned reservation. Payment authorization and inventory finalization can fail independently; define retry, eventual reconciliation, and refunds/voids for authorization that succeeds but cannot lead to a sale. Never assume an external PSP can be rolled back by a database transaction.

Verify MassTransit version licensing at implementation time. Prefer at-least-once delivery with durable inbox/outbox and deduplication rather than an inaccurate exactly-once delivery claim.
