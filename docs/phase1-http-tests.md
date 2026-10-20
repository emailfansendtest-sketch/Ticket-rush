# Phase 1 final HTTP validation

This addition does not modify application behavior, database migrations, domain types, or cryptography package pins. It adds `Microsoft.AspNetCore.Mvc.Testing` **10.0.13** to Central Package Management and references the API project from the test project.

The original two HTTP integration tests use `WebApplicationFactory<Program>` and Testcontainers PostgreSQL, with `TestDatabaseInitializer.InitializeAsync()` creating the schema and seeding 100 demo seats. No Docker Compose database is reused.

The test factory explicitly resolves the API content root at `src/TicketRush.Api` relative to `TicketRush.slnx`, rather than relying on the default assumption that the API project is directly below the solution root. No `.sln` file is required.

## Apply

Extract the patch into the folder containing `TicketRush.slnx`, preserving relative paths. Do not delete or replace production project code. Your two security package pins in `Directory.Packages.props` remain untouched.

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx
dotnet test TicketRush.slnx --filter FullyQualifiedName~ApiContractIntegrationTests
dotnet test TicketRush.slnx
```

**Original expected total:** 24 tests (22 service/database tests + two API tests), developer-verified on October 20, 2026. The Phase 1 completion patch adds two expiry tests (26 total expected) and extends the Location assertions to follow the newly implemented GET resources. See `docs/phase1-completion.md`.

After these checks pass, mark Phase 1 as complete for its defined local proof-of-correctness scope, commit the test evidence, and begin Phase 2 by designing independently owned Inventory and Ordering storage plus the message/outbox workflow. Do not separate their transaction before specifying saga compensation and idempotent messaging.
