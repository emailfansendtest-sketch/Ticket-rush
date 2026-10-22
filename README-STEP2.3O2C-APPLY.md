# Phase 2.3o.2c — Physical Inventory database separation

**Apply over the committed, verified Phase 2.3o.2b + Inventory DI fix baseline.**

The patch changes where the authoritative Inventory state lives. **Do not run `docker compose up -d --build` immediately after applying it to an existing deployment.** Follow the maintenance-window procedure to avoid accidentally serving an empty seat catalog or losing access to existing holds.

## Scope

- Adds `TicketRush.Inventory.Persistence` / `InventoryDbContext`, independent EF migrations, and an Inventory migration job.
- Moves Inventory entity mappings, inbox/outbox/sale-ledger persistence and dispatcher out of the old shared infrastructure assembly. Classes deliberately retain their existing namespaces so the old tests and transitional Booking context still compile.
- Makes Inventory HTTP API, consumer and result publisher use only `ConnectionStrings__InventoryDb`.
- Keeps `BookingDbContext` as a **legacy compatibility subclass** of `InventoryDbContext`, adding only the old synchronous `orders` aggregate. The normal distributed runtime does not use this subclass.
- Adds an isolated Inventory PostgreSQL container, a one-time transactional streaming data-copy tool and integration tests.
- Updates Inventory-directed verification scripts to query the new PostgreSQL instance.

**The source Booking DB and its tables are not deleted or modified by the cutover.** The legacy schema/migrator remains for old Phase 1 test and compatibility paths.

## Apply the ZIP overlay and remove moved source copies

The patch **moves 14 files** from `TicketRush.Infrastructure` into `TicketRush.Inventory.Persistence`. A ZIP extraction does not remove old files automatically. After unpacking over your repository, run:

```powershell
.\Apply-Phase2.3o.2c.ps1
```

The Infrastructure `.csproj` also excludes these old paths as a temporary safeguard if they remain on disk. `git add -A` will record their deletion and the new project files.

## Prerequisites and build

- Save/push the last passing commit on a clean branch. The previously tested 2.3o.2b and DI fix must be present.
- Make sure Docker Desktop runs, the dev hold-proof key exists, and `docker compose config --quiet` succeeds.
- Use local-only development credentials as in existing Compose; never expose these databases publicly.

```powershell
$ErrorActionPreference = 'Stop'
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
```

**Do not proceed if any test fails.** The new `InventoryDatabaseSeparationTests` require two temporary PostgreSQL containers.

## Existing development deployment: safe cutover

1. Arrange a **maintenance window**. Stop incoming checkout requests and ongoing writers before copying:

```powershell
docker compose stop api inventory-api inventory-consumer inventory-result-outbox `
  ordering-api ordering-outbox ordering-saga payment-worker notification-worker
```

This pauses the Ordering outbox and saga so new Inventory work cannot be produced. RabbitMQ stays running; queued commands and results remain pending. Ensure any in-flight requests have stopped, and check for unexpected writers before continuing.

2. **Back up all relevant PostgreSQL databases**, especially the source Booking and independent Ordering/Payment databases. Example of a raw development SQL dump (adjust path and credentials to your setup):

```powershell
New-Item -ItemType Directory -Force .\backups | Out-Null
# Use cmd redirection to preserve raw UTF-8 pg_dump output in Windows PowerShell 5.1.
cmd /c "docker compose exec -T postgres pg_dump -U ticketrush -d ticketrush -Fp > backups\booking-pre-inventory-cutover.sql"
if ($LASTEXITCODE -ne 0) { throw 'Booking backup failed' }
```

Do not reset or remove existing Docker volumes. Save backups in a secure location outside version control.

3. Build and start **only** the new Inventory database and its dedicated schema migrator; no Inventory writers yet:

```powershell
docker compose up -d inventory-postgres
docker compose build inventory-migrate inventory-cutover
docker compose run --rm inventory-migrate
```

`SEED_DEMO_DATA` is deliberately `false`. The new Inventory DB must be empty except for the migration-created schema. **Do not seed it before copying.**

4. Copy the old Inventory state in a single destination transaction:

```powershell
$env:INVENTORY_CUTOVER_CONFIRMED='YES'
docker compose --profile cutover run --rm --no-deps inventory-cutover
Remove-Item Env:INVENTORY_CUTOVER_CONFIRMED
```

The copier reads `events`, `holds`, `seats`, `inventory_sales`, `inventory_inbox` and `inventory_result_outbox` from the old Booking database. It streams CSV between connections without materializing whole tables, verifies table row counts, and commits destination rows **together**. `xmin` is never copied: it is a database-local MVCC system column. `orders` is intentionally **not** copied; authoritative distributed orders already live in Ordering PostgreSQL.

It refuses a nonempty destination, a schema with pending migrations, or identical source and target addresses. On failure, it rolls back the destination import and leaves the old database unchanged. After a **successful** import, do not rerun the tool against the nonempty target.

5. Start the switched services and run regression smoke scripts:

```powershell
docker compose up -d --build
.\scripts\Verify-Inventory-Database-Separation.ps1
.\scripts\Verify-Legacy-Checkout-Retired.ps1
.\scripts\Verify-Inventory-Api-Bridge.ps1
.\scripts\Verify-Inventory-Consumer.ps1
.\scripts\Verify-Checkout-Api-Saga.ps1
.\scripts\Verify-Payment-Void-Compensation.ps1
.\scripts\Verify-Notification-Service.ps1
```

Additionally run the scripts relevant to Inventory result-outbox replay and saga recovery after the principal smoke tests pass. The original Booking DB should remain untouched by new distributed reservations.

## Fresh development deployment (no historical Booking data)

Run `docker compose up -d --build`; this initializes an **empty** Inventory DB. To create the same 100-seat demo event after ensuring the DB is new/empty, run:

```powershell
docker compose run --rm -e SEED_DEMO_DATA=true inventory-migrate
```

Do not run the one-time cutover in this case. For an existing Booking deployment, follow the maintenance-window steps instead.

## Recovery and rollback warning

Until resumed Inventory services process **new** bookings, the original Booking DB remains a fallback. Once new holds, sales, inbox receipts or outbox publications occur on Inventory PostgreSQL, switching the running services back to the original Booking DB is **not safe without an explicit reconciliation procedure**. It could lose new sales or cause double-selling. If post-cutover verification fails, stop writers/consumers and preserve **both** databases and RabbitMQ state. Do not run `docker compose down -v`.

## Validation status

The patch is source-reviewed and its XML/YAML structure has been checked, but the authoring environment has no .NET SDK or Docker engine, so the executable build and tests are **unverified**. Report compiler errors or smoke failures before committing.
