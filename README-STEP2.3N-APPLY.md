# Phase 2.3n — simulated Notification service (isolated increment)

Apply the patch over **the committed Phase 2.3m solution with the PaymentVoidedV1 saga-deserializer fix**.
The patch includes only changed or newly added files. It does not introduce a real email or SMS provider.

## Behavior

Ordering creates exactly one `CheckoutNotificationRequestedV1` outbox message within the transaction that first transitions a checkout to `Completed` or `Failed`:

- Inventory committed => `Completed` notification.
- Inventory rejected => `Failed` notification (possibly while Payment void compensation is still Pending).
- Payment declined => `Failed` notification (no Inventory sale requested).

**Compensation void completion does not send a second notification** in this phase. The simulated notification describes the terminal business checkout result, not the payment-compensation status. Existing synchronous `POST /orders` is unchanged; this phase concerns the asynchronous Ordering saga.

Ordering's existing outbox publisher routes `CheckoutNotificationRequestedV1` to the durable RabbitMQ `ticket.notification.checkout-finalized.v1` queue using routing key `notification.checkout-finalized.v1`. The new Notification Worker consumes and writes a durable receipt to its own PostgreSQL database in a single transaction, then ACKs the message. Duplicates preserve a single receipt per order and MessageId. An invalid message is dead-lettered. Unexpected database failures are requeued, not classified as invalid payloads.

No external delivery is attempted. The receiver only simulates notification delivery by persisting the receipt and writing a structured log. A real email/SMS integration would need a provider-specific idempotent delivery strategy and monitoring.

## Commands from repository root (PowerShell / Windows)

```
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
```

Check that EF model matches its new migration:

```
dotnet ef migrations has-pending-model-changes `
  --project .\src\TicketRush.Notification.Persistence `
  --startup-project .\src\TicketRush.Notification.Persistence `
  --context NotificationDbContext
```

Provision the independent Notification database and worker alongside the existing stack:

```
docker compose up -d --build
docker compose ps -a
docker compose logs notification-migrate notification-worker ordering-outbox
```

Then run:

```
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Notification-Service.ps1
```

Expected `Notification service RabbitMQ smoke: PASS`.
The new smoke test inserts a **synthetic completed checkout and a matching outgoing Notification event** atomically into the Ordering database. It then waits for both RabbitMQ publication (confirmed via Ordering outbox) and the Notification database receipt. Finally, it republishes the same existing outbox row to check deduplication. **It does not demonstrate a real seat sale or send an actual notification.** Separate PostgreSQL integration tests exercise the actual Ordering saga terminal-result code paths.

Rerun the existing checkout/payment/compensation smoke scripts to check regressions as appropriate.

## Security and limits

The Notification database is separate from Ordering and listens on 127.0.0.1:5443 for local development only. RabbitMQ still uses shared local-development credentials. Delivery is at least once, not exactly once; the durable receipt deduplicates successful simulated processing. Repeated internal failures can cause a hot-message retry loop with the current prefetch=1 policy; bounded retry/quarantine and metrics remain production hardening work.

This phase does not change EF models in Ordering, Payment or Inventory; only Notification introduces an EF migration.
