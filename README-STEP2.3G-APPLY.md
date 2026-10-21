# Phase 2.3g — durable saga timeout/recovery

Apply this patch **on top of your committed, validated Phase 2.3f** repository (71 tests, HTTP saga smoke PASS). Do not overwrite unrelated edits.

## What changes

- Adds the `SagaRecoveryDispatcher` to Ordering persistence and `SagaRecoveryWorker` to the existing Ordering saga host.
- Adds three Ordering `order_intents` columns and an EF Core migration: `recovery_attempts`, `next_recovery_at_utc`, `recovery_needs_attention`.
- HTTP GET/POST status projections append `recoveryAttempts` and `recoveryNeedsAttention` while retaining existing fields.
- Polls every 5 seconds, at most 25 overdue orders per poll. Defaults: 120-second stale threshold, 30-second initial recovery retry, 10-minute cap, 5 attempts. Configurable via `SagaRecovery__InitialTimeoutSeconds`, `SagaRecovery__RetryBaseSeconds`, `SagaRecovery__RetryMaxSeconds`, `SagaRecovery__MaxAttempts` on `ordering-saga`.
- Requeues the **same durable message** in Ordering's outbox: original `CheckoutRequestedV1` if no Inventory command exists, or the existing `InventorySaleRequestedV1` otherwise. Does not create new message IDs or change `CheckoutState`.
- Uses `FOR UPDATE SKIP LOCKED` for watchdog replica safety and row locks to serialize recovery with Inventory result handling.
- On exhaustion, flags `recoveryNeedsAttention` while checkout remains **Pending**. A late real Inventory event can still mark Completed/Failed and clears the attention flag.
- Adds 8 PostgreSQL Testcontainers integration tests and a synthetic overdue-checkout recovery smoke script that does **not sell a seat**.

## Critical principle

A timeout cannot prove a sale was unsuccessful. Never mark `Failed` or release a seat based only on elapsed time. Database inboxes deduplicate safe replays. The watchdog does **not** implement a distributed timeout cancellation/compensation protocol.

## Validation

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
# Expected test count: 79 (71 baseline + 8)

dotnet ef migrations has-pending-model-changes `
    --project .\src\TicketRush.Ordering.Persistence `
    --startup-project .\src\TicketRush.Ordering.Persistence `
    --context OrderingDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs ordering-migrate ordering-saga ordering-outbox

powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
```

Expected recovery smoke: `Ordering saga recovery smoke: PASS`.

The recovery smoke inserts a synthetic old Ordering intent and published checkout fact in **disposable local development data**. The worker recovers the missed event, Inventory rejects the nonexistent hold, and Ordering moves to Failed based on **that Inventory result**, not due to timeout. Production/default threshold is 120 seconds; synthetic test record is 5 minutes old, so the test does not need to wait two minutes.

## Remaining limitations

- Temporary retries and attention flags are operational safety tools, not a full compensation protocol.
- After maximum attempts, the worker does not retry that order anymore, but a queued outbox delivery may still complete and the result consumer remains active.
- Manual operator actions / alert integrations and distinct RabbitMQ permissions remain future work.
- The existing BuyerId/hold authorization weakness in the local-only HTTP API is unchanged.
- The publisher performs at-least-once delivery; replays can be observed by consumers, which must remain idempotent.
- .NET SDK and Docker are unavailable in the authoring environment; build/tests/migrations/broker execution are **not claimed** here.

## Operator inspection (local PostgreSQL)

```sql
SELECT id, hold_id, state, recovery_attempts, next_recovery_at_utc, recovery_needs_attention
FROM order_intents
WHERE state = 0
ORDER BY recovery_needs_attention DESC, requested_at_utc;
```

Investigate `recovery_needs_attention=true` before taking any compensating action.
Never assume Inventory failed solely from the lack of a response.
