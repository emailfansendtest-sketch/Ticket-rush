# Phase 2.3e — Ordering saga wiring (local validation required)

Apply this patch on top of your current validated Phase 2.3d revision (whether or not it has already been pushed). It introduces:

- `OrderingSagaProcessor` in Ordering persistence: converts a durable `CheckoutRequestedV1` fact into one `InventorySaleRequestedV1` outbox message; consumes confirmed/rejected Inventory results into a durable Ordering result inbox and terminal `OrderIntent.State`.
- `TicketRush.Ordering.SagaWorker` independent host, with separate CheckoutRequested and InventoryResult RabbitMQ subscriptions.
- A third Ordering database migration, `OrderingSagaResultInbox`, without modifying the Booking/Inventory database schema.
- Extension of the existing Ordering publisher to route Inventory commands to the **existing Inventory queue with matching dead-letter queue arguments**.
- Eight PostgreSQL integration tests (expected solution total: 65) and a full message-driven local smoke test.

## Local checks (Windows PowerShell)

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project src/TicketRush.Ordering.Persistence `
  --startup-project src/TicketRush.Ordering.Persistence `
  --context OrderingDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs ordering-saga ordering-outbox inventory-consumer inventory-result-outbox
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Outbox.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga.ps1
```

**Warning:** `Verify-Ordering-Saga.ps1` sells one available seeded demo seat. Use only disposable local data. The smoke inserts a synthetic, internally consistent Ordering checkout intent/fact because the independent async CheckoutIntakeService is not exposed over HTTP yet. Neither this increment nor the script implements payments.

**Scope:** The legacy public `POST /orders` is NOT switched. There is no payment or notification simulator, no compensation and no automatic timeout for abandoned Pending intents. Invalid saga messages are broker-confirmed into a durable quarantine queue; unknown unsolicited result messages will be quarantined instead of mutating OrderIntents. The standard Inventory result publisher smoke inserts a synthetic rejection for a nonexistent OrderIntent; with the saga worker active, that synthetic message will be quarantined as intended.

**Operational concern:** The simple correctness-first consumer requeues transient exceptions without a bounded retry count. Poison handling, monitoring, and queue-level retry policies are deferred.
