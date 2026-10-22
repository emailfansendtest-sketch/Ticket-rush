# Phase 2.3p — Inventory consumer outage and duplicate replay smoke

**Prerequisite:** Phase 2.3o.2c is deployed against a correctly initialized `ticketrush_inventory` PostgreSQL database. The user has already reported passing local unit/integration tests, the database cutover, and the existing smoke scripts.

This is the next **operational recovery demonstration**, not another schema migration. The patch adds one version-controlled PowerShell script to reproduce two meaningful failure conditions against the live distributed checkout: (1) Inventory's command consumer is offline when a paid checkout is queued, and (2) the exact same durable sale command is published again after an order has completed.

## Files

- `scripts/Verify-Inventory-Consumer-Outage-Recovery.ps1` — new executable smoke test.
- `README-STEP2.3P-APPLY.md` — this guide.

No database migrations, C# source changes, or Docker Compose configuration changes are required. Extract the ZIP over the **current validated repository**; do not remove or reset any Docker volumes.

## What the script actually proves

1. Checks that all required local services are running and Inventory uses `InventoryDb` instead of `BookingDb`.
2. Creates a **real authenticated one-seat hold**, including the signed proof, using the Booking -> Inventory HTTP bridge.
3. Stops `inventory-consumer` **before** submitting checkout and confirms it has stopped.
4. Submits the paid sandbox checkout and waits for Ordering's Inventory command outbox row to be **broker-confirmed**, while the order remains `Pending` and Inventory has no inbox/sale for that order.
5. Restarts `inventory-consumer`, waits for `Completed`, and verifies the durable records.
6. Clears the published flag only for **this test order's existing** `InventorySaleRequestedV1` outbox row, causing an intentional duplicate publish with the **same MessageId** and payload. It does **not** insert another order or a new command.
7. Verifies that Inventory re-publishes the original result and Ordering ACKs it, while the databases continue to show **one** order, Inventory sale/inbox/outbox, Payment authorization and notification receipt.
8. Verifies that the legacy Booking DB did not acquire this order.
9. Uses `finally` to restart the stopped Inventory consumer if any step fails.

This verifies a specific at-least-once/restart scenario. It does **not** prove every possible crash point or global exactly-once message delivery. RabbitMQ may deliver messages more than once; business actions must remain idempotent.

## Required environment

- **Local disposable development data only**. The test intentionally pauses one consumer, writes an order and permanently sells one available demo seat.
- Docker Desktop, running `docker compose up -d --build`, the Keycloak demo buyer Alice, and an available seat in event `11111111-1111-1111-1111-111111111111`.
- RabbitMQ Management UI reachable at `http://localhost:15672`; development-only credentials are `ticketrush:localdevonly`.
- `scripts/Get-Dev-BuyerToken.ps1` and the previous Phase 2.3o.2c verification scripts present.
- Run in an isolated Compose stack, with no concurrent external booking or queue traffic; the script checks the Ordering results queue drains to zero.

## Run

```powershell
# From the TicketRush repository root:
docker compose config --quiet
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

.\scripts\Verify-Inventory-Consumer-Outage-Recovery.ps1 -AllowDisruption
```

Optional URL and timeout parameters:

```powershell
.\scripts\Verify-Inventory-Consumer-Outage-Recovery.ps1 `
    -AllowDisruption `
    -BookingUrl 'http://localhost:8088' `
    -OrderingUrl 'http://localhost:8091' `
    -TimeoutSeconds 120
```

**Expected output:**

```text
PASS: Inventory command broker-confirmed; order pending; no Inventory write.
Original Inventory command rearmed for one deliberate duplicate delivery...
Inventory consumer outage and duplicate replay: PASS
```

The script intentionally modifies exactly one Ordering outbox row associated with its newly created test order to request redelivery. Do not run it against production or valuable test data.

## If it fails

The `finally` handler attempts to restart `inventory-consumer`. Confirm manually:

```powershell
docker compose ps inventory-consumer
docker compose up -d --no-deps inventory-consumer
docker compose logs --tail=100 inventory-consumer ordering-outbox inventory-result-outbox ordering-saga
```

**Do not rerun the full script to resume an unfinished test order.** The original test order may remain Pending, and the hold can expire. Inspect the checkout status and PostgreSQL/RabbitMQ state first. Keep both the Ordering and Inventory database volumes; never use `docker compose down -v` as a recovery procedure.

## Follow-on stage

After this smoke passes, the next increment should cover **actual broker unavailability and outbox retry** (stop/restart RabbitMQ), then service readiness, metrics and trace propagation. That closes Phase 2 operability before Phase 3 load/scaling experiments.

## Validation status

Source/scripts were reviewed and packaged, but PowerShell, Docker and .NET runtime execution were not available in the authoring environment. **Local execution remains required**.
