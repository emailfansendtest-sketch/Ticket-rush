# Phase 2.3o.1 — Inventory-owned sale ledger (preparation for persistence separation)

**Prerequisites:** Phase 2.3n has been committed and locally validated with
`Verify-Notification-Service.ps1` passing. This is an **incremental patch**, not
the completed Inventory database split.

## What changes

- Async `InventorySaleRequestedV1` processing now atomically writes `inventory_sales`
  instead of an extra legacy `orders` row. The Ordering aggregate remains owned by
  the independently deployed Ordering database.
- Same PostgreSQL transaction still atomically changes the hold and seats, saves
  `inventory_inbox`, persists Inventory sale evidence, and inserts the result outbox.
- `InventorySaleRecord.OrderId` is the correlation ID received from Ordering, **not**
  a foreign key to a cross-service table; `HoldId` is unique and references Inventory's
  local hold. The legacy synchronous `POST /orders` still writes `TicketOrder`.
- EF migration `20261022080000_InventoryOwnsSaleRecords` adds the new table and
  backfills historical async sales identified by the exact `distributed:{order-id}`
  legacy idempotency key. **It deliberately preserves old `orders` rows.** Historic
  duplication will be cleaned up only in a separate, validated data migration.
- Existing Inventory consumer, Ordering checkout, payment, void and notification
  message contracts remain unchanged.
- Integration tests and the six distributed-workflow smoke scripts now inspect
  `inventory_sales` rather than assuming async sales are legacy orders.

**Still not done:** this does **not** extract a separate Inventory PostgreSQL
instance. `InventoryService`, `InventorySaleCommandProcessor` and the result-outbox
publisher still use `BookingDbContext`; the legacy synchronous API and the Inventory
workers therefore still share a database. That is the following increment.

## Apply and validate — from the repository root, Windows PowerShell

1. Ensure your checked-out repository includes the pushed Phase 2.3n state.
2. Copy the patch files over repository files, preserving relative paths.
3. **Back up important local data** before a schema upgrade. The migration only adds
   a table and copies historical rows; it does not delete historical orders.
4. Build/test/verify EF metadata before changing a running stack:

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project .\src\TicketRush.Infrastructure `
  --startup-project .\src\TicketRush.Infrastructure `
  --context BookingDbContext
```

5. On an **existing running Compose environment**, first quiesce the old
   Inventory sale consumer. Otherwise it could write old `orders` rows after the
   one-time backfill, leaving them absent from the new ledger. Messages accumulate
   safely in RabbitMQ while the consumer is stopped:

```powershell
docker compose stop inventory-consumer
docker compose build migrate inventory-consumer inventory-result-outbox
docker compose run --rm migrate
docker compose up -d --build
docker compose ps -a
docker compose logs migrate inventory-consumer inventory-result-outbox
```

For a **fresh environment with no running containers**, simply run
`docker compose up -d --build` after tests; the `migrate` dependency ensures schema
creation precedes Inventory workers. Do not clear existing volumes just to apply this migration.

Then run the existing smoke scripts relevant to the async flow, particularly:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Consumer.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Notification-Service.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Void-Compensation.ps1
```

Note that most demos allocate seats; using a heavily consumed seed database may
make tests fail for lack of inventory. Prefer a clean local test database or reseed
explicitly; `docker compose down -v` is **destructive**.

## Acceptance criteria

- Testcontainers race, replay, and rollback tests remain green.
- Migration snapshot matches the EF model; upgrade test backfills previous async sales.
- A new async sale yields: `inventory_sales=1`, `inventory_inbox=1`,
  `inventory_result_outbox=1`, `orders=0` for that OrderId. A legacy synchronous
  confirmation still produces its expected `orders` row.
- Replayed sale command cannot create an additional sale or outbox event.
- Checkout, compensation and Notification remain unchanged end to end.

## Proposed next step: Phase 2.3o.2

Move the Inventory-owned entities and migrations into `TicketRush.Inventory.Persistence`
and a dedicated Inventory database. Migrate hold creation and reads to an Inventory API
or distributed commands, retire the synchronous shared-transaction `POST /orders`
path, transfer/cut over existing data, and update Docker Compose/migration and integration
boundaries. Avoid switching worker connection strings to a new database until these
prerequisites have been implemented and verified.
