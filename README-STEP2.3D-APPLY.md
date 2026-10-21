# TicketRush Phase 2.3d — Inventory result outbox publisher

Apply the Phase 2.3d patch **over the committed and validated Phase 2.3c solution**. The patch adds one worker project, an Inventory outbox dispatcher, one BookingDbContext migration, tests and documentation. It also includes the confirmed Windows PowerShell array fix in `scripts/Verify-Inventory-Consumer.ps1` that was already applied and tested locally after the original 2.3c ZIP.

Before testing, ensure `src/TicketRush.Ordering.OutboxPublisher/Program.cs` still has `using Microsoft.Extensions.Configuration;` from your previous local correction. The Phase 2.3d patch does not overwrite that file.

Build and migration checks:

```powershell
docker compose config --quiet
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx -c Release
dotnet test .\TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project .\src\TicketRush.Infrastructure `
  --startup-project .\src\TicketRush.Infrastructure `
  --context BookingDbContext
```

**Expected tests: 57** (52 verified through Phase 2.3c plus 5 new tests). The new migration is `20261021180000_InventoryResultDeliveryScheduling`. If the EF model drift test fails, treat that as a blocker and report the drift; do not create another migration blindly.

Docker and real RabbitMQ smoke:

```powershell
docker compose up -d --build
docker compose ps -a
docker compose logs migrate inventory-result-outbox
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Outbox.ps1
```

Expected: `Inventory result outbox RabbitMQ smoke: PASS`.

The new service `inventory-result-outbox` consumes the transitional Booking database, not a separate Inventory database. Result events are published to a durable queue for Ordering, **but no Ordering saga result consumer is implemented yet**. The existing HTTP booking/checkout behavior is untouched.

A host-side `.NET` SDK, Docker and PowerShell were unavailable where this patch was prepared. Static checks do not prove compilation or runtime behavior. Review and run all commands locally before committing.
