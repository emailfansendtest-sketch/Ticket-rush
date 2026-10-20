# Phase 2 step 2.2 — Catalog extraction (instructions)

Apply this patch to the reviewed Step 2.1 working tree **at the solution root**.

**Delete** the obsolete file after extraction:

```
src/TicketRush.Catalog/EfEventCatalog.cs
```

That old class uses BookingDbContext; its replacement in this step is
`src/TicketRush.Catalog/HttpEventCatalog.cs`. The new independent EF implementation
is `src/TicketRush.Catalog.Storage/EfEventCatalog.cs`.

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

docker compose up -d --build
docker compose ps
docker compose logs catalog-migrate migrate
Invoke-RestMethod http://localhost:8090/events
Invoke-RestMethod http://localhost:8088/events
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
```

The Phase 1 smoke script validates the Booking HTTP flow with the forwarded
Catalog endpoint. The new service can also be run directly from Visual Studio
if you start `catalog-postgres` and apply its migrations first.

```powershell
dotnet ef migrations list --project src/TicketRush.Catalog.Storage `
  --context CatalogDbContext
```

The new migration is included in source. Do **not** run `migrations add InitialCreate`
again. Unlike the old Booking database, the new Catalog database should be empty
before its first migration. This step leaves the Booking database, its migrations,
its demo event registry and the proven checkout transaction untouched.

Tests require Docker/Testcontainers. The Catalog-to-Booking forwarding regression
uses an in-process Catalog TestServer, plus a real PostgreSQL Testcontainer, while
intentionally configuring a non-existent Booking database. The earlier Booking
HTTP tests use a fixed Catalog test double to keep their focus on booking behavior.

Rollback strategy: revert the code/Compose change and redeploy the old Phase 1 API.
Do not delete the new Catalog Docker volume if it contains any data you need.

## Dependency failure behavior

The Booking `GET /events` facade has a 10-second Catalog HTTP timeout. Upstream network/HTTP failures produce HTTP 503, and timeouts produce HTTP 504. The forwarding test suite checks a simulated network failure without needing an actual outage or Booking database. These mappings are local-demo behavior; a full resilience policy, metrics, and readiness probes are later steps.
