# Phase 2.3d — Inventory result publication

This increment activates the transactional result outbox created in Phase 2.3c. It **does not** implement Ordering saga result consumption or change the existing synchronous `/orders` behavior.

## Components

- `TicketRush.Infrastructure.InventoryResultOutboxDispatcher` claims due Inventory outbox rows using PostgreSQL `FOR UPDATE SKIP LOCKED` inside a transaction.
- `TicketRush.Inventory.ResultPublisher` is a separate hosted worker, with a RabbitMQ 7.2.2 publisher using mandatory routing, durable topology and tracked publisher confirmations.
- RabbitMQ direct exchange `ticket.events` has a durable `ticket.ordering.inventory-results.v1` queue bound to `inventory.sale-committed.v1` and `inventory.sale-rejected.v1`.
- `MessageId` and `MessageType` headers and the original JSON payload are preserved. Duplicate publications are possible on uncertain acknowledgment or DB commit failure. The future Ordering consumer **must deduplicate by message ID**.
- `InventoryResultDeliveryScheduling` adds `publish_attempts`, `next_attempt_at_utc`, `last_error` and an index. Unsent result rows created by 2.3c remain eligible after migration.

## Delivery flow

1. Phase 2.3c consumer commits seat state, inbox receipt and result-outbox event atomically in the transitional Booking PostgreSQL database.
2. Result publisher claims one due row and holds its row lock during publication (15-second bound).
3. On confirmed broker ACK, it marks `published_at_utc` in the database; on failure it stores retry metadata and schedules exponential retry in 2–60 seconds.
4. A broker ACK followed by failed DB commit may cause the same message to be published again.

The worker opens a broker connection/channel per publication, matching the Ordering publisher's correctness-first design. It is not optimized for throughput and it cannot guarantee exactly-once delivery. Poison messages can be retried indefinitely. Failure recovery, performance tuning, and DLQ policy are later hardening tasks.

## Validation

```
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes --project src/TicketRush.Infrastructure --startup-project src/TicketRush.Infrastructure --context BookingDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs migrate inventory-result-outbox
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Outbox.ps1
```

The smoke test inserts **one synthetic rejected result**, creates a temporary RabbitMQ probe queue bound to the rejection key, waits for the exact message in RabbitMQ, checks the DB published timestamp, and removes its probe queue. It does not change seats. The result **also remains in the permanent durable queue**, awaiting an Ordering result consumer. Do not use the script against production data.

The test suite adds five dispatcher integration tests (previous validated total 52; expected total 57). The result worker has no live-broker integration test in xUnit; the PowerShell smoke exercises real broker delivery.
