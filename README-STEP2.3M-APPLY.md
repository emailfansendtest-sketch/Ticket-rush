# Phase 2.3m — Simulated Payment authorization void compensation

Apply this patch over the **validated and committed Phase 2.3l** TicketRush repository.

## Scope and invariants

* When an authorized Payment is followed by `InventorySaleRejectedV1`, Ordering atomically marks checkout `Failed`, records the original rejection in its inbox, and writes **one** `PaymentVoidRequestedV1` command to its transactional outbox. **No seat was sold.**
* Payment handles the void command idempotently in its **own database**. It locks the original authorization receipt, verifies buyer/order/authorization identity, sets `voided_at_utc`, persists an inbox receipt, and writes **one** `PaymentVoidedV1` result in the **same Payment transaction**. No real funds were ever captured, transferred, or refunded.
* Ordering consumes the void confirmation into a distinct durable inbox, and projects `compensationStatus` as `NotRequired`, `Pending`, `Voided`, or `NeedsAttention` while leaving the checkout's business state `Failed`.
* A dedicated watchdog handles overdue failed checkouts with unconfirmed voids. It republishes the **same** durable void command ID; Payment re-arms its **original** confirmed result and Ordering deduplicates it. Exhaustion raises `recoveryNeedsAttention` rather than claiming a successful void.
* Existing successful sales and declined Payment checkouts do not issue voids. Historical Inventory-only workflows are unchanged. Payment authorizations are **not** captured or charged.

## Schema changes

Two additive EF migrations:

* `OrderingPaymentVoidInbox`: `ordering_void_inbox` keyed by message ID, unique order ID.
* `PaymentAuthorizationVoid`: `voided_at_utc` on Payment receipt, `payment_void_receipts`, and a change from the single `payment_outbox(order_id)` unique index to `payment_outbox(order_id, message_type)` so an authorized result and a subsequent void result can coexist.

The Payment index migration's `Down` can fail after a void has been written, because restoring the old unique constraint requires deleting additional outbox rows. **Treat downgrading a database containing void results as a data migration**, not an automatic rollback. For local development, restore a database backup or recreate disposable data rather than silently removing events.

## Validation (PowerShell, solution root)

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes --project .\src\TicketRush.Payment.Persistence --startup-project .\src\TicketRush.Payment.Persistence --context PaymentDbContext
dotnet ef migrations has-pending-model-changes --project .\src\TicketRush.Ordering.Persistence --startup-project .\src\TicketRush.Ordering.Persistence --context OrderingDbContext
docker compose up -d --build
docker compose ps -a
docker compose logs ordering-migrate payment-migrate ordering-saga ordering-outbox payment-worker
```

New end-to-end synthetic compensation smoke test (no seat sold):

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Void-Compensation.ps1
```

Expected: `Payment authorization void compensation smoke: PASS`.

Regression smoke tests:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Simulation.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Payment-Decline.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Recovery.ps1
```

The standard successful checkout smoke sells one demo seat; the compensation smoke uses a nonexistent hold and never sells one.

## Non-goals and limitations

* This is a **simulated void**, not a refund or payment-provider API operation. A real gateway requires explicit gateway idempotency tokens, provider reconciliation, and separation of authorization, capture and release.
* Ordering still trusts messages on its RabbitMQ result queues; broker ACLs and stricter event provenance are future hardening.
* Temporarily failing processors may requeue repeatedly; the watchdog provides *separate* operational attention for overdue compensation, not a generic poison-message retry policy.
* No Notification service, independent Inventory DB, Kubernetes, or horizontal load testing here.
* This archive was structurally audited but **not compiled or executed** in the authoring environment; the local tests, migration checks and broker smoke test are required before commit.
