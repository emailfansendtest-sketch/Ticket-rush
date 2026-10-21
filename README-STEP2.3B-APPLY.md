# TicketRush Phase 2.3b — patch instructions

**Baseline:** reviewed Phase 2.3a durable Ordering intake, 40 tests passed.

Apply this patch at the directory containing `TicketRush.slnx`. The patch changes `Directory.Packages.props`, `TicketRush.slnx`, `compose.yaml`, the Ordering persistence model, snapshot, and the existing Ordering migration test, and adds the publisher worker, next EF migration, dispatch tests, and operational documentation. It does not modify Catalog, Inventory, Booking/HTTP checkout, or the previous `OrderIntakeInitialCreate` migration.

The source ZIP was based on the previously provided audited 2.3a archive plus the GSSAPI workaround in Compose; if you made other local changes to `compose.yaml`, inspect the diff before overwriting it.

In PowerShell, from the solution root, validate Compose **before** applying the new stack. The corrected layout has exactly one top-level `rabbitmq` service and one `ordering-outbox` service, while `catalog-api` still depends on `catalog-migrate`:

```powershell
docker compose config --quiet
docker compose config --services

dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
# expected: 44 passing tests (40 existing + 4 dispatch tests)

dotnet ef migrations has-pending-model-changes `
  --project src/TicketRush.Ordering.Persistence `
  --startup-project src/TicketRush.Ordering.Persistence `
  --context OrderingDbContext

docker compose config --quiet
docker compose up -d --build
docker compose ps -a
docker compose logs ordering-migrate ordering-outbox rabbitmq
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Outbox.ps1
```

`ordering-migrate` should exit successfully. `ordering-outbox` and `rabbitmq` should keep running. `Verify-Ordering-Outbox.ps1` inserts a **synthetic** Pending order/outbox record only and waits for the outbox publisher to mark it delivered. It does not sell Inventory seats. If the RabbitMQ broker is stopped during an attempt, retry metadata is persisted; restarting the broker should allow eventual publication without deleting the Ordering volume.

`rabbitmq` management UI: `http://localhost:15672` (local development credentials `ticketrush`/`localdevonly`).

**Limits:** No actual RabbitMQ-backed integration test is included in this patch: dispatch tests use a fake publisher, with the Compose smoke script providing manual live-broker evidence. `CheckoutIntakeService` is not yet exposed by HTTP; its messages will be consumed in a later milestone. Publication is **at-least-once**, and downstream deduplication by `MessageId` is mandatory.
