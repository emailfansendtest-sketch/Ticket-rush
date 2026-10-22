# Phase 2.3r — Ambiguous publisher ACK / CheckoutRequested replay

**Applies on top of verified and pushed Phase 2.3q.** This patch adds tests and scripts only. There are no application-source, EF migration, Compose, or configuration changes.

## What is already tested, and what is missing

`OrderingOutboxDispatchTests.Broker_ack_followed_by_database_failure_may_redeliver_same_message` already emulates broker acceptance followed by failed PostgreSQL publication-mark persistence. This increment closes the next gap: **does republishing the original `CheckoutRequestedV1` after an actual terminal checkout repeat Payment, Inventory, or Notification work?**

New coverage:

- `OrderingCheckoutFactReplayTests`: PostgreSQL-backed integration tests for identical checkout-fact replay after **Completed** and after Payment **Failed** terminal states. Confirm no second business command, notification, or inbox row.
- `Verify-Checkout-Fact-Ambiguous-Ack-Replay.ps1`: Docker Compose end-to-end test using a *real authenticated hold and simulated purchase*. It makes the checkout complete, stops `ordering-saga`, resets **only this order's** already-published `CheckoutRequestedV1` outbox mark, waits for a second broker-confirmed publication of the **same MessageId**, verifies the duplicate is queued, restarts the saga, and checks that the duplicate is consumed without changing downstream business state.

This recreates the **observable consequences** of an ACK-then-DB-commit failure; it does not pretend to force a genuine network-level ACK ambiguity. At-least-once transport is expected. 'Exactly-once delivery' is not claimed.

## Risks, preconditions, and safety

- **Disposable, idle local Compose stack only**, no concurrent checkouts or external workloads.
- Runs a *real simulated purchase*, permanently consuming one available demo seat. Does **not** reset databases or delete records.
- Modifies `published_at_utc` and `next_attempt_at_utc` in the Ordering outbox for the original event belonging to this test order. This is deliberate fault injection: do not use against shared/production data.
- Temporarily stops `ordering-saga`, affecting all Ordering saga queues. A `finally` block attempts to restore it even after a failed assertion. If stopping/restarting fails, inspect the service manually.
- Avoid running the script while other tests are manipulating the broker, ordering-saga, or Ordering DB.
- Requires a successfully completed Phase 2.3o.2c Inventory database cutover and prior Phase 2.3p/2.3q verification.
- If a previous run failed, it may have consumed its chosen demo seat or left a pending duplicate. **Do not rerun blindly.** Examine `docker compose ps`, Ordering outbox rows, and the RabbitMQ queue first.

## Apply

Extract this archive at the **TicketRush repository root**, preserving relative paths. It adds two new files under `tests/` and `scripts/` respectively, and this guide.

```powershell

dotnet build TicketRush.slnx -c Release

dotnet test TicketRush.slnx -c Release --no-build `
  --filter "FullyQualifiedName~OrderingCheckoutFactReplayTests|FullyQualifiedName~OrderingOutboxDispatchTests"

dotnet test TicketRush.slnx -c Release --no-build

docker compose up -d

.\scripts\Verify-Checkout-Fact-Ambiguous-Ack-Replay.ps1 -AllowDisruption
```

Optional: `-TimeoutSeconds 240` if the local workstation needs more time (maximum 300; 10-minute holds still apply).

## Success criteria

The smoke script requires:

1. Authenticated hold and completed checkout using the normal APIs.
2. Exactly one original `CheckoutRequestedV1` with a broker-confirmed `published_at_utc`.
3. A second broker-confirmed publication of that **same** event with `publish_attempts` increased.
4. A message waiting in the checkout-fact queue while the saga consumer is stopped; later the queue drains after restart.
5. Exactly one Ordering intent, Payment command, Inventory command, Notification command, Payment inbox record, Inventory result inbox record, payment receipt, Inventory sale/inbox/outbox record, sold seat, and simulated Notification receipt for the order.
6. No new order in the legacy Booking DB and checkout status remains Completed.

Expected last line:

```
Checkout fact ambiguous-ACK replay: PASS
Order: ... | Original MessageId: ... | Publish attempts: ... -> retry | Sales: 1 | Notifications: 1
```

On failure, inspect:

```powershell
docker compose ps
docker compose logs --tail 150 ordering-outbox ordering-saga rabbitmq payment-worker inventory-consumer notification-worker
```

## Git commit message (after local validation)

```
test(messaging): verify idempotent checkout fact replay (Phase 2.3r)

- Add PostgreSQL integration tests for CheckoutRequestedV1 replay after terminal saga states
- Verify completed and declined checkouts do not emit duplicate commands or notifications
- Re-publish the original checkout fact with its stable MessageId in a local smoke drill
- Assert broker confirmation, saga consumption, and unchanged business outcomes
- Verify one Ordering, Payment, Inventory, and Notification record per checkout
- Require explicit opt-in and restore the Ordering saga worker on failure
- Document ambiguous-ACK simulation scope and recovery troubleshooting

Demonstrate end-to-end idempotency when a broker-confirmed outbox event is sent again.
```

## Scope and limitations

The project already has an integration test for the exact publisher/DB timing window. Here the added test is about **downstream duplicate effects**. The management API queue-drain check is evidence of consumption, not proof of a unique delivery audit log. A future fully instrumented fault-injection harness could track individual RabbitMQ ACKs and crashes more directly; that is outside this patch.
