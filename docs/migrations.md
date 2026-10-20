# TicketRush — switch from bootstrap SQL to EF Core migrations

This migration preparation is based on **TicketRush(intermediate_19.10.2026).zip**. The booking logic and newer tests developed after that archive were **not** supplied as an updated ZIP. Use the **migration patch**, which leaves `BookingService.cs` and your test logic untouched, to update your current working copy.

## What is changed

- `BookingDbContext` mapping is the schema source of truth. The old `001_schema.sql` has been retired (copy in `docs/legacy/001_schema.original.sql` for comparison only).
- Adds missing FK constraints (`seats.event_id`, `holds.event_id`, `orders.hold_id`) and status/count check constraints; retains the `xmin` concurrency-token mapping.
- Adds `Microsoft.EntityFrameworkCore.Design` 10.0.0 and a locally pinned `dotnet-ef` 10.0.0 tool manifest.
- Adds design-time factory for `BookingDbContext`.
- Adds `TicketRush.DbMigrator` as a one-shot deployment job and moves demo seeding into `DemoDataSeeder`.
- Updates Docker Compose: PostgreSQL starts empty, migrator applies EF migrations, API starts after successful migration.
- Replaces handwritten-schema initialization in integration tests with `MigrateAsync()` and `DemoDataSeeder.SeedAsync()`; includes a schema/migration smoke test.

**Important:** This package DOES NOT contain the generated EF migration classes yet. Only `dotnet ef migrations add InitialCreate` on a machine with the .NET 10 SDK can reliably generate and validate their model snapshot. Neither .NET SDK nor Docker is installed in the artifact-creation environment. Do not run `docker compose up --build` until you have generated and committed `InitialCreate`.

## Applying to your latest working source

1. Commit or back up your current project first.
2. Extract `TicketRush_Migrations_Patch.zip` over your current TicketRush **solution root**, keeping all existing unmentioned files.
3. **Do not overwrite** your current `BookingService.cs` or `BookingConcurrencyTests.cs`; the patch intentionally excludes both.
4. In Visual Studio 2026 Developer PowerShell at solution root, execute:

```powershell
powershell -ExecutionPolicy Bypass -File ./scripts/Convert-TestInitialization.ps1
powershell -ExecutionPolicy Bypass -File ./scripts/Generate-InitialMigration.ps1
```

5. Inspect `src/TicketRush.Infrastructure/Migrations/*InitialCreate.cs` and its `.Designer.cs`/`BookingDbContextModelSnapshot.cs`. Confirm creation of `events`, `seats`, `holds`, `orders`, the unique indexes, `xmin` version mapping, expected FK/check constraints. Expect no explicit physical `xmin` column creation.
6. On a disposable test/demo database, **reset the old handwritten-schema Docker volume** before starting the new approach. This deletes demo data:

```powershell
docker compose down -v
docker compose up --build -d
```

7. Run integration tests (Docker must be running):

```powershell
dotnet test ./TicketRush.slnx
```

8. After all tests pass, remove `deploy/postgres/001_schema.sql` from your working tree, update any old README references, and commit the migration classes and model snapshot. The old DDL is preserved for comparison under `docs/legacy/001_schema.original.sql`.
9. Check for model drift whenever updating entity mappings:

```powershell
dotnet ef migrations has-pending-model-changes --project ./src/TicketRush.Infrastructure --startup-project ./src/TicketRush.Api
```

## Integration tests: what changes?

Replace **every** occurrence of this pattern (including your newest tests):

```csharp
await using (var setup = new BookingDbContext(options))
    await setup.Database.ExecuteSqlRawAsync(
        await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "001_schema.sql")));
```

or the braced variant with:

```csharp
await TestDatabaseInitializer.InitializeAsync(options);
```

The initializer creates tables via migrations and seeds the same fixed demo event and 100 seats the SQL file previously inserted. No `EnsureCreated()` call should remain. The conversion PowerShell script handles both patterns; inspect its diff before committing.

## IMPORTANT database safety

**Never** run the initial `CreateTable` migration blindly against an existing database containing the original SQL-created tables: EF migrations history will not know they exist and schema creation will fail. For disposable development DBs use `docker compose down -v`. For valuable persistent data design a reviewed baseline/adoption migration, back up the data first, and do not fabricate `__EFMigrationsHistory` rows.

Run a single migration job per deployment, not inside every API replica. EF Core 9+ supports migration locks, but keeping database schema changes independent of ordinary API startup makes deployment behavior easier to reason about. Migrate-only job has an opt-in `SEED_DEMO_DATA=true` for local Compose; do not enable it in production.

## Existing application code

The current booking logic uses `db.GetEntitySchema<T>()` for SQL identifiers. This continues to use the EF model: no need to duplicate table/column names in raw SQL. Reservation and checkout services are deliberately **not changed** by this migrations patch.

## Known validation limitation

This package has been statically inspected and its XML/JSON structure validated. Actual C# compilation, `dotnet ef migrations add`, PostgreSQL migrations, and Docker Testcontainers execution must be performed on your development machine. After running the scripts, please share the generated migration or any compiler error for review.
