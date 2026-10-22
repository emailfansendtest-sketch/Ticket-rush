# Phase 2.3o.2b — Retire shared-transaction checkout from the public Booking gateway

**Prerequisite:** Phase 2.3o.2a applied and validated, including its bridge and downstream saga, payment-void and notification smoke tests.

## Why this increment is necessary

The Booking API still exposed `POST /orders`, which creates a legacy `TicketOrder` and changes Inventory seats **in one shared Booking PostgreSQL transaction**. This would allow the Booking API and the new Inventory service to compete for ownership during a future Inventory database cutover. The **Ordering-owned asynchronous** `/checkouts` API already replaces it.

This patch **retires the legacy synchronous routes in the normal Compose deployment**, and removes the Booking PostgreSQL connection string and migration dependency from that gateway. No Inventory tables are moved yet; Inventory API and workers still share the Booking database. Consequently **this is an application-boundary increment, not completion of independent Inventory persistence**.

## Changes

- `LegacyBooking:Enabled` is **false by default**. The normal Booking API uses HTTP to Inventory and has no `BookingDbContext` registration or DB credentials.
- Authenticated `POST /orders` and `GET /orders/{id}` return HTTP **410 Gone**, with an instruction to use Ordering's signed-proof `/checkouts` workflow. This deliberately does NOT forward blindly: the old request lacks the signed hold proof required by the asynchronous API.
- Only explicitly enabled **Development** compatibility mode registers the original in-process Inventory/Ordering services, database access and legacy routes. The existing isolated Phase 1 contract tests opt in, so their original behavior remains tested.
- Added `LegacyCheckoutRetirementTests` to verify the normal gateway has no Booking DbContext registration, rejects anonymous calls, and returns 410 to authenticated callers.
- Added a non-destructive `Verify-Legacy-Checkout-Retired.ps1` smoke test.
- No EF migrations, data moves, schema deletion, or new Docker volumes.

## Applying

Extract the ZIP over your committed, locally validated **Phase 2.3o.2a** repository. Preserve relative paths.

```powershell
# From repo root

docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

docker compose up -d --build
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Legacy-Checkout-Retired.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Api-Bridge.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Consumer.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Void-Compensation.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Notification-Service.ps1
```

**Important:** Old Phase 1 HTTP smoke scripts that post directly to Booking `/orders` are now expected to fail in the normal Docker deployment. This is intentional. Do not reset any volumes: current payment/order/inventory state should be preserved and verified by the later scripts.

## Safe rollback

This patch has no database schema/data migration. If it fails validation, restore your previous commit and rebuild the Booking API container. The still-running Inventory/Ordering services remain untouched.

## Next: 2.3o.2c — physical Inventory database cutover

1. Introduce Inventory-owned DbContext, migrations, and migration job, without `TicketOrder` as a business entity.
2. Provide offline, repeatable data-transfer tooling for existing `events`, `seats`, `holds`, `inventory_sales`, `inventory_inbox`, and `inventory_result_outbox`; never reinitialize or silently discard sold seats and delivery evidence.
3. **Stop all Inventory writers and publishers and drain in-flight commands** before snapshotting source tables. Compare totals, per-event seat states, hold IDs, inbox/outbox IDs, sold order IDs, unique constraints and pending outbox states before re-enabling writes.
4. Point Inventory API, Inventory consumer and result publisher at **one** new Inventory DB, with separate migration history and credentials. Update all SQL-inspecting smoke scripts.
5. Prove old Booking DB downtime doesn't disrupt new Inventory holds or saga processing, and that historical records survive restart/replay. Only then decommission the original Booking DB.

## Proposed commit message (use after verification)

```text
refactor(booking): retire synchronous checkout in distributed deployment (Phase 2.3o.2b)

- Disable legacy /orders routes in the normal Booking gateway with HTTP 410 responses
- Remove Booking DB configuration and migration dependency from the gateway container
- Keep shared-transaction checkout available only in explicit development compatibility mode
- Preserve signed hold-proof and Ordering /checkouts saga contracts
- Add gateway retirement regression tests and non-destructive smoke verification

Prepare the system for a safe Inventory-owned database cutover without changing data.
```

**Verification:** The patch was statically reviewed and structurally checked by its author, but the current environment has no .NET SDK or Docker to execute tests; local validation is required.
