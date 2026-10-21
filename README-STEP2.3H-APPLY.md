# Phase 2.3h — recover missing Inventory result messages

Apply on top of your committed Phase 2.3g solution, whose baseline is **79 tests** and a passing saga-recovery smoke test. Changes are intentionally limited to Inventory duplicate-command processing, its integration tests, and a new end-to-end recovery smoke test. **No database schema change or new worker is needed.**

## Problem addressed

Previously, when Ordering replayed `InventorySaleRequestedV1` after a timeout, Inventory's durable inbox acknowledged the duplicate without restoring an already published `InventorySaleCommittedV1` or `InventorySaleRejectedV1`. If Ordering had missed the result, it could remain Pending permanently.

A valid duplicate Inventory command now checks its original inbox receipt, identifies the original Inventory result by its **stable ResultMessageId**, and makes that existing result-outbox row eligible for re-publication **only if it was previously marked published**. The existing result-outbox worker then re-publishes the original payload and MessageId with RabbitMQ publisher confirmation. Ordering's result inbox deduplicates any result it had already received.

The conditional parameterized PostgreSQL `UPDATE` takes a row lock, so it serializes with `InventoryResultOutboxDispatcher`'s `FOR UPDATE SKIP LOCKED`. If the result is still pending or has a scheduled failed-publication retry, the replay does not reset its retry schedule. A missing/mismatched result outbox record is reported as an invariant violation rather than silently acknowledged.

**No seat is sold a second time**, no new result event is inserted, and neither business state nor the contract shape changes on duplicate processing. This is at-least-once publication, not exactly-once. Duplicate commands may cause duplicate result events; those are safe for the Ordering result inbox.

## Validation

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
# Expected: 85 tests (79 baseline + 6 new cases)

docker compose up -d --build
docker compose ps -a
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Recovery.ps1
```

The new smoke script uses **synthetic rejection records**, not real seat sales. It seeds a previously processed Inventory command, marks its result as previously broker-confirmed, and seeds a 5-minute-old Pending Ordering intent with a previously published Inventory command. The Ordering recovery watchdog requeues the original command, Inventory restores the *same* result event, and Ordering transitions to Failed **because of Inventory's rejection**, not the timeout. The test verifies `publish_attempts >= 2`, one original Inventory inbox receipt and result, one Ordering result receipt, and no local sale. It works with disposable local Docker Compose data only.

## Intentional remaining limitations

- Real RabbitMQ result loss is unusual with durable queues, but this verifies the recovery mechanism under an injected lost-delivery condition.
- If Inventory's inbox exists but its result outbox has been destroyed/corrupted, the replay is rejected; it does not invent a new result. Operational intervention is necessary.
- If the original Ordering recovery attempt limit was already exhausted, automatic recovery remains stopped; the operator must intervene.
- Unlimited broker replays of a valid already-processed command can cause repeated result publication. The Ordering inbox still deduplicates; additional operational rate limiting and broker permissions remain future work.
- The locally exposed HTTP buyer/hold ownership weakness remains a separate outstanding issue.
- .NET and Docker are not installed in the authoring environment: build, integration tests, and live smoke **must be executed locally** before committing.
