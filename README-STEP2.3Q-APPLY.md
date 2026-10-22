# Phase 2.3q — RabbitMQ outage / transactional outbox recovery

## What this patch does

Adds `scripts/Verify-RabbitMq-Outage-Outbox-Recovery.ps1` to the **already validated and committed Phase 2.3p** repository. No C# source, schemas, containers, or existing behavior are changed.

This is an **end-to-end fault-injection smoke test** of an actual temporary RabbitMQ outage:

1. Select one available demo seat and create a signed hold as Alice.
2. Stop `rabbitmq` before accepting the HTTP checkout.
3. Submit the checkout to Ordering. Require HTTP 202, showing that the transaction did not depend on the broker being online.
4. Observe the original `CheckoutRequestedV1` outbox row **unpublished**, with a failed publish attempt (`last_error`) persisted.
5. Verify the order is still Pending and no Payment or Inventory sale exists.
6. Restart *only* RabbitMQ. Do not manually restart publishers/consumers; they must recover themselves.
7. Verify the same outbox event is broker-confirmed on a later attempt, then verify exactly one completed order, payment authorization, Inventory sale, and Notification receipt.
8. Confirm that this checkout does not write a legacy Booking order.

## Prerequisites and risk

- **Disposable local Docker Compose environment only.** The script temporarily stops the *whole* RabbitMQ broker, affecting all services using it, and permanently consumes one demo seat.
- The Phase 2.3o.2c independent Inventory database cutover, Phase 2.3p regression test, and their smoke scripts must already have passed.
- The existing services should be started and idle. Do **not** run other checkouts or recovery drills concurrently.
- Do not reset Docker volumes to make the test pass. Provide an available demo seat instead.
- A failure during the test leaves the Booking hold and/or a Pending order; the script deliberately does not delete these business records. Inspect the state before retrying.
- The test has one global timeout after reserving a seat; default is 240 seconds to stay within the hold's 10-minute expiration window. An unhealthy broker restart or slow development PC may still make a checkout fail, which should be diagnosed rather than ignored.

## Apply

Extract the ZIP at the solution root, preserving relative paths. No migration or Compose rebuild is needed.

```powershell
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
docker compose up -d
.\scripts\Verify-RabbitMq-Outage-Outbox-Recovery.ps1 -AllowDisruption
```

Optional: `-TimeoutSeconds 300` to allow a slow local workstation up to 5 minutes. The script attempts to restore RabbitMQ in a `finally` block, even if a test assertion fails. It intentionally does not restart other services to avoid masking reconnection problems.

Expected final output:

```
RabbitMQ outage + transactional outbox recovery: PASS
Order: ... | Seat: A-... | Outbox retry confirmed | Sales: 1 | Notifications: 1
```

If it fails, inspect (in particular) the original outbox row and publisher logs:

```powershell
docker compose ps
docker compose logs --tail 150 rabbitmq ordering-outbox ordering-saga payment-worker inventory-consumer inventory-result-outbox notification-worker
```

## What this tests — and what it doesn't

The test demonstrates a committed outgoing event surviving **broker unavailability prior to publication**, followed by delayed retry and eventual saga completion. It does not emulate a crash after RabbitMQ ACK but before PostgreSQL publication-state commit; that separate ambiguous-ACK scenario requires its own targeted test. It does not prove exactly-once transport or infinite availability.

## Git commit message (after local verification)

```
test(messaging): verify RabbitMQ outage and outbox recovery (Phase 2.3q)

- Add disruptive RabbitMQ outage and restart smoke test
- Verify authenticated checkout commits with broker offline
- Assert unpublished CheckoutRequestedV1 remains durable after failed publish
- Confirm outbox retries and publishes the original event after recovery
- Check one completed Ordering saga, Payment authorization, and Inventory sale
- Verify one Notification receipt and no legacy Booking order
- Restore RabbitMQ in finally and document safety requirements

Demonstrate end-to-end recovery from broker unavailability without lost business events.
```
