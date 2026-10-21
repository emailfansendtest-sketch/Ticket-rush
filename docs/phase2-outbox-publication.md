# Phase 2.3b — RabbitMQ outbox publisher

This increment adds *publisher only*. It does **not** connect the HTTP `/orders` endpoint to `CheckoutIntakeService`, nor implement Inventory consumption or sagas. Existing booking checkout remains unchanged.

## Guarantees

1. `CheckoutIntakeService` commits the Pending order intent and its `CheckoutRequestedV1` outbox record in one Ordering PostgreSQL transaction.
2. The publisher uses a transaction with `SELECT ... FOR UPDATE SKIP LOCKED` to claim one due row. Competing worker replicas skip locked rows. It holds the PostgreSQL row lock while waiting for the broker, a deliberate correctness-first choice.
3. RabbitMQ exchange and queue are durable; JSON messages are persistent; publishing uses `mandatory: true` and publisher confirmation tracking. A broker NACK, returned message, timeout or connection loss **never** sets `PublishedAtUtc`.
4. Failed attempts store `PublishAttempts`, `NextAttemptAtUtc` (exponential 2–60 second delay) and a truncated diagnostic error. These metadata fields are part of the second Ordering EF migration, `OutboxDeliveryScheduling`.
5. After confirmation, `PublishedAtUtc` is committed. If the broker ACK succeeds but PostgreSQL commit fails, the message can be sent again: **at-least-once, not exactly-once**. Downstream consumers MUST deduplicate by stable `MessageId` before performing effects.

## Implementation notes

- Publisher currently opens a fresh RabbitMQ connection/channel for each message. This simplifies failure isolation but limits throughput; a future measured optimization can reuse channels and connections with explicit reconnection logic.
- An empty outbox causes a one-second poll delay. Failed attempts reschedule individual rows; there is no permanent dead-letter state in this increment. Operators can inspect `last_error` and `publish_attempts` in `outbox_messages`.
- The publisher declares an initial durable queue `ticket.ordering.checkout-requested.v1` bound to direct exchange `ticket.events` and routing key `checkout.requested.v1`. This keeps the messages available for the Inventory consumer to be introduced later. Retain the same binding when adding that consumer.
- Consumer acknowledgments, inbox deduplication, message replay behavior, payment processing, a durable saga, and Inventory service ownership are explicitly **not implemented** yet.
- Docker credentials are local-development-only. The RabbitMQ management page is bound to loopback: `http://localhost:15672` (`ticketrush` / `localdevonly`). Broker AMQP traffic is internal to Compose and not exposed to the host.

## Validation

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project src/TicketRush.Ordering.Persistence `
  --startup-project src/TicketRush.Ordering.Persistence `
  --context OrderingDbContext

docker compose config --quiet
docker compose up -d --build
docker compose ps -a
docker compose logs ordering-migrate ordering-outbox rabbitmq
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
```

Run `powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Outbox.ps1` after Compose startup to inject a synthetic outbox entry and confirm it reaches the broker. This adds a test-only Pending order to the local database and does not sell seats.

Tests inject a fake `IOutboxPublisher` to verify claim exclusivity, retry, eventual success and at-least-once redelivery. **Those tests do not establish real-broker behavior**; inspect the RabbitMQ management UI, and perform a RabbitMQ interruption/recovery exercise before accepting the runtime deployment. **The Phase 1 smoke script does not create any Ordering outbox records.**

Connection strings: after the earlier nonfatal `libgssapi_krb5.so.2` warning was investigated, Compose uses `GSS Encryption Mode=Disable` for local PostgreSQL clients; this is unrelated to RabbitMQ TLS. Do not expose this demo publicly.
