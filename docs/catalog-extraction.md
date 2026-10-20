# Catalog module extraction — corrected Phase 1 increment

This is a **small modular-monolith refactoring**, not a claim that Phase 1 is complete.

## Module dependency direction

- `TicketRush.Api` references `TicketRush.Catalog` and the existing shared `TicketRush.Infrastructure` (for Inventory/Ordering endpoints not yet extracted).
- `TicketRush.Catalog` owns its interface, response model **and query implementation**. It references the Phase-1 shared `TicketRush.Infrastructure` for `BookingDbContext`.
- `TicketRush.Infrastructure` does **not** reference `TicketRush.Catalog`. The integration test guards this.
- This is a temporary shared-database compromise. Future independent services require distinct ownership/persistence; do not split checkout transactions in Phase 1.

Only `GET /events` is moved to the Catalog HTTP adapter. `GET /events/{eventId}/seats` remains Inventory behavior. Paths and JSON fields are preserved.

## Preserved

`Directory.Packages.props` (including the cryptography transitive pins), `Directory.Build.props`, `BookingService`, `BookingDbContext`, migrations, the DbMigrator, seed data, all existing booking tests, and Docker Compose are unchanged.

## Verify

With Docker Desktop running:

```powershell
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx
dotnet test .\TicketRush.slnx
```

Perform a manual API smoke test of `GET /events`; the added test checks the Catalog implementation using migrated PostgreSQL.

## Limitation

This is a minimal Catalog read boundary, not full modular isolation. Catalog still depends on the shared `BookingDbContext`, and the `TicketEvent` domain class remains in the common Domain project. Phase 1's Inventory/Ordering ownership remains unfinished.
