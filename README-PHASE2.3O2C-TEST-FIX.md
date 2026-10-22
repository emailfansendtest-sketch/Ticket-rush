# Phase 2.3o.2c — fix five integration-test regressions

**Apply over the Phase 2.3o.2c patch** after running its `Apply-Phase2.3o.2c.ps1` relocation script. This is a source/test-only overlay: no schema migration, database copy, Docker configuration change, or volume reset is required.

## Root causes

1. `InventoryService` now requires `InventoryDbContext`. The legacy `ApiContractIntegrationTests` host registered `BookingDbContext` only. That type derives from `InventoryDbContext`, but Microsoft DI does not automatically register its base type. The test factory now explicitly registers `InventoryDbContext` as an alias of the *same scoped* `BookingDbContext`. The normal application's **explicit Development-only legacy registration** also gets the same alias to support manual compatibility mode. Default distributed gateway operation is unchanged.
2. `SELECT to_regclass('public.orders')` returns a PostgreSQL `regclass` type, which `ExecuteScalarAsync` cannot materialize as `System.Object` with the Npgsql provider in this project. The test now selects `to_regclass(...) IS NULL` so PostgreSQL returns a normal `boolean`.

## Changed files

- `src/TicketRush.Api/Program.cs`
- `tests/TicketRush.IntegrationTests/ApiContractIntegrationTests.cs`
- `tests/TicketRush.IntegrationTests/InventoryDatabaseSeparationTests.cs`

## Apply

Extract the ZIP at the **TicketRush repository root**, preserving the relative paths. Do not run the database cutover yet if it has not already been done.

## Tests

```powershell
dotnet build TicketRush.slnx -c Release

dotnet test TicketRush.slnx -c Release --no-build `
  --filter "FullyQualifiedName~ApiContractIntegrationTests|FullyQualifiedName~InventoryDatabaseSeparationTests"

dotnet test TicketRush.slnx -c Release --no-build
```

Only after the whole test suite passes, follow the original `README-STEP2.3O2C-APPLY.md` instructions. Existing deployments require the stated maintenance-window backup, migration, one-time data copy, and smoke validation **in that order**. Do not reset existing database volumes.

If the existing deployment has **already** been switched to the new Inventory database, these test-only/legacy-mode changes do not require another cutover. Do not rerun a completed nonempty data copy.

## Commit message (after verification)

```text
fix(inventory): repair legacy DI alias and Inventory schema test (Phase 2.3o.2c)

- Resolve InventoryDbContext through the scoped BookingDbContext in legacy mode
- Restore shared-transaction compatibility in Phase 1 API integration tests
- Read PostgreSQL to_regclass existence check as a boolean
- Keep normal database-free Booking gateway and Inventory cutover unchanged
```

**Validation:** Source-level checks performed only. The generating environment has no .NET SDK or Docker engine; runtime tests have not been executed here.
