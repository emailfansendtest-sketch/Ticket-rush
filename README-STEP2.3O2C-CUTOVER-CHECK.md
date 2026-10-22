# Phase 2.3o.2c — optional read-only Inventory snapshot verification

This *additive* patch contains a **pre-restart verification script**, not a replacement for the authoritative Phase 2.3o.2c cutover runbook. It does not move tables or change any application code, EF Core migrations, or Docker configuration.

## Why

`Verify-Inventory-Database-Separation.ps1` checks the running services and target schema **after** the services restart. It does not compare the historical Booking database with the new Inventory database. `Verify-Inventory-Cutover-Copy.ps1` is meant to run after the one-time InventoryDataCopier succeeds **and while all writers remain stopped**.

## Usage (existing development database)

1. Follow `README-STEP2.3O2C-APPLY.md`: stop writers, back up databases, start the isolated Inventory database, run the Inventory schema migrator, and run the confirmed one-time cutover.
2. BEFORE `docker compose up -d --build`, run:

   ```powershell
   .\scripts\Verify-Inventory-Cutover-Copy.ps1
   ```

3. It checks that writer containers remain stopped, validates six table counts, compares available/held/sold seat counts, confirmed holds, and result-outbox publication/attempt state, and requires there is no legacy `orders` table in the new Inventory database.
4. Only if the script passes, restart services and run `Verify-Inventory-Database-Separation.ps1` plus existing Booking, Inventory, Saga, Payment and Notification smoke scripts.

This script is **not idempotent operationally**: its comparisons cease to be meaningful after the destination begins receiving writes. It is safe to execute repeatedly while services remain stopped because it only runs `SELECT` queries.

## Important safeguards

- Not a substitute for `pg_dump` backups, careful stopping of producers/consumers, and the one-time cutover tool's transaction.
- No `docker compose down -v`. Do not overwrite a nonempty Inventory target or recopy after resumed writes.
- A passed snapshot check does not guarantee all business semantics. Full smoke checks remain mandatory for the cutover.
- This patch does not require a new database migration or a fresh .NET test run. The existing changed .NET code is already tested; the new PowerShell script runs only when performing a real cutover.
