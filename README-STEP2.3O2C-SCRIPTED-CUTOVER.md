# Phase 2.3o.2c — scripted one-time Inventory cutover

This patch turns the hand-typed data-copy command from `README-STEP2.3O2C-APPLY.md` into a dedicated, source-controlled PowerShell script. It also includes the unchanged read-only snapshot verification script to make the handoff self-contained. **It does not alter source code, Compose, migrations, or database schema.**

## Use only for an existing development database

1. From the repository root, follow the original runbook's maintenance steps. Stop incoming booking traffic and all the writer services; let in-flight operations finish. Back up the source Booking database and independent Ordering/Payment (and other important) databases. Verify that you can locate and restore the backups. Do **not** use `docker compose down -v`.
2. Start the destination database and run the dedicated schema migrator **without seeding**:

   ```powershell
   docker compose up -d inventory-postgres
   docker compose build inventory-migrate inventory-cutover
   docker compose run --rm inventory-migrate
   if ($LASTEXITCODE -ne 0) { throw 'Inventory migration failed' }
   ```

3. Run the new script from the repository root:

   ```powershell
   .\scripts\Invoke-Inventory-DataCutover.ps1 -BackupConfirmed -ConfirmCopy
   ```

   The script verifies writer containers are stopped, both database containers are running, the old Inventory has rows, the destination Inventory tables are empty, and then runs the `inventory-cutover` .NET tool with `INVENTORY_CUTOVER_CONFIRMED=YES` set **only for that invocation**. It checks the Docker exit code, resets the environment variable, and runs `Verify-Inventory-Cutover-Copy.ps1` before reporting success.

4. **Only after success** resume the services, then run the existing distributed smoke scripts:

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

## Limits / safety

- `-BackupConfirmed` is an operator attestation, not an automated backup or restore check.
- The script deliberately does **not** automatically stop services or restart them; those are explicit decisions in the maintenance runbook.
- The script also does **not** migrate/create the destination schema: run `inventory-migrate` first. Its preflight query fails closed if the schema is absent.
- The one-time C# copier's database transaction and migration checks are unchanged.
- The snapshot script verifies counts and selected state distributions; it is not a byte-for-byte or full foreign-key integrity comparison.
- A successful data copy must **not** be rerun, especially after new bookings are processed on Inventory PostgreSQL. On a post-copy failure, stop and investigate rather than resetting Docker volumes.
- Skip this script entirely for a clean installation with no historical source Inventory data; follow the fresh-install path in the original runbook.
- This PowerShell code was checked by inspection; it could not be executed here because PowerShell and Docker are unavailable in the authoring environment.
