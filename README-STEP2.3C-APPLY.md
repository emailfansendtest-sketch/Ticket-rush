# TicketRush Phase 2.3c — Inventory inbox consumer

Apply `TicketRush_Phase2_Step3c_Inventory_Inbox_Consumer_Patch.zip` to the solution root from the **committed, validated Phase 2.3b** branch. This patch does not replace `TicketRush.Ordering.OutboxPublisher/Program.cs`, where you previously fixed the missing `Microsoft.Extensions.Configuration` using directive.

The code is additive. It does not replace the Booking HTTP API, existing Ordering checkout path, or Ordering's RabbitMQ publisher. It introduces a new Inventory process that subscribes to `InventorySaleRequestedV1`, and an inbox + result outbox in the transitional Booking database. **Ordering does not publish these commands yet.** The smoke test creates one command explicitly.

## Validation in PowerShell

```powershell
docker compose config --quiet
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx -c Release
dotnet test .\TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project .\src\TicketRush.Infrastructure `
  --startup-project .\src\TicketRush.Infrastructure `
  --context BookingDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs migrate inventory-consumer
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Outbox.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Consumer.ps1
```

**Expected integration test count: 52**, based on 44 previously reported passing tests plus eight new command-processor tests. Each Testcontainers test requires Docker Desktop.

The Inventory smoke script consumes one available seat from the seeded demo event. It requires RabbitMQ management API at `localhost:15672` and a running Inventory consumer. Do not use against important data or publicly exposed services.

The EF migration is checked in as `20261021040000_InventoryInboxAndOutbox.cs` with `DbContext` and `Migration` attributes on the migration class rather than a generated designer companion. The context model snapshot has been updated in source. **Because this environment cannot run the .NET SDK, use `has-pending-model-changes` to validate it before accepting the patch.** If it reports drift, treat that as a blocker and share the diff rather than applying an additional migration blindly.

## Scope boundary

A confirmed Inventory sale is recorded in the legacy Booking database's `orders` table to preserve its relational constraints and existing tests; this is explicitly transitional. New Inventory results remain in `inventory_result_outbox` awaiting the subsequent result publisher. No saga, distributed order completion, or real checkout HTTP route is switched on yet.

## Additional review fix

The Inventory consumer reconnect loop now observes the RabbitMQ 7.x `AsyncEventingBasicConsumer.UnregisteredAsync` event as well as channel shutdown. A server-side `basic.cancel` can stop deliveries while leaving the channel open, so waiting only on `ChannelShutdownAsync` would leave the worker idle indefinitely. The reconnect-delay path also terminates normally on host cancellation. The database processor, EF model/migration, and message contract are unchanged. Live cancellation recovery has not been executed in this environment.
