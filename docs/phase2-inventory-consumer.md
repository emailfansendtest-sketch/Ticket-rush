# Phase 2.3c: Inventory Sale Command Consumption

**Scope.** An independent RabbitMQ consumer process uses the existing Booking PostgreSQL database as Inventory's transitional source of truth. The public Booking API and Ordering's synchronous checkout are unchanged. No ordering saga is enabled in this step.

## Protocol and topology

- Exchange: `ticket.events` (direct, durable, already created by Ordering outbox publisher).
- Command routing key: `inventory.sale-requested.v1`.
- Consumer queue: `ticket.inventory.sale-requested.v1` (durable, manual ACK, prefetch 1).
- Dead-letter exchange: `ticket.dead`; dead-letter queue: `ticket.inventory.sale-requested.dead.v1`.
- `InventorySaleRequestedV1` is **not** currently emitted by Ordering. The test or the next saga phase supplies it explicitly.
- The worker reconnects both after channel shutdown and broker-initiated consumer cancellation (which does not necessarily close the channel). This recovery path still requires a live broker test.


## Database transaction

A received command is validated and claimed in `inventory_inbox` (unique `message_id` and `order_id`). The same PostgreSQL transaction acquires row locks on hold and seats using the existing `IInventoryCheckout.PrepareCheckoutAsync`, creates a transitional local sale record in `orders` if successful, and persists **one** `InventorySaleCommittedV1` or `InventorySaleRejectedV1` into `inventory_result_outbox`.

Only after the transaction commits does the RabbitMQ consumer ACK. Duplicate delivery (including a different message ID with the same order ID and matching hold/buyer) does nothing. Reuse of a message/order identity for a different command is treated as invalid, not silently successful.

The `orders` table remains a **transitional local sale record** because the proven Phase 1 schema relies on it. `OrderingDbContext` is not used by the Inventory consumer. Removing this transitional coupling must be planned before retiring the monolith, not while Phase 1 regression tests depend on it.

The result outbox is intentionally **not published yet**; an Inventory result-outbox dispatcher is the next step before a saga can depend on completion events. This phase tests the receive/commit/ACK boundary, not end-to-end saga completion. Poison messages are dead-lettered. Transient failures NACK/requeue; a production version will need bounded delay/dead-letter policies to avoid pathological redelivery loops.

## Validate

```
docker compose config --quiet
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes --project src/TicketRush.Infrastructure --startup-project src/TicketRush.Infrastructure --context BookingDbContext
docker compose up -d --build
docker compose logs inventory-consumer migrate
powershell -ExecutionPolicy Bypass -File ./scripts/Verify-Inventory-Consumer.ps1
```

The smoke script sells one **available seeded demo seat**. It publishes the same command twice and checks one Inbox, one result outbox, and one Inventory-side local order. Do not use it on non-disposable databases.

### Delivery guarantees

RabbitMQ guarantees neither exactly-once processing nor exactly-once publication. This implementation uses manual ACK **after PostgreSQL commit**, durable inbox uniqueness, and transactional Inventory results. Broker redelivery after a successful DB commit is harmless. If the DB fails before commit, the message is retried. If an unexpected infrastructure exception persists, the current consumer will retry indefinitely; operations/alerting remain a later hardening item.
