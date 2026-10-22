# Phase 2.3o.2a — Inventory HTTP service boundary (transitional)

**Prerequisite**: applied, tested, committed Phase 2.3o.1 (Inventory sale ledger).

## What changed

- Introduces `TicketRush.Inventory.Api`, a separately deployed ASP.NET Core host for seat maps, hold creation, and owner-restricted hold reads.
- Moves the public Booking API's **read/hold** requests through `HttpInventoryService` in Docker Compose. Booking continues issuing signed hold proofs so Ordering's established `/checkouts` flow is unchanged.
- Validates JWT independently in Inventory API. Booking forwards the incoming bearer token **only** on authenticated hold requests. Inventory rejects forged buyer IDs and doesn't issue hold proofs.
- Preserves original public Booking endpoints and response JSON/HTTP contract; legacy synchronous `/orders` still uses in-process `IInventoryCheckout` and `BookingDbContext`.
- Adds HTTP adapter tests, real-PostgreSQL Inventory API integration test, Compose setup, and `Verify-Inventory-Api-Bridge.ps1`.

**Important: this is NOT a physical database split.** Both APIs and the existing Inventory RabbitMQ workers still access the same Booking PostgreSQL database. This is safe for the existing transaction semantics but is a transitional state. No schema migration is necessary in this patch.

## Apply

Copy the patch files over the **Phase 2.3o.1** source (preserving paths). From repository root:

```powershell
# Review resolved service dependencies and catch Compose syntax errors.
docker compose config --quiet

dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

docker compose up -d --build
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Api-Bridge.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Consumer.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Void-Compensation.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Notification-Service.ps1
```

Use a database with available demo seats; scripts consume holds and orders. No existing database volume should be reset for this patch. **No data migration is necessary.**

### Smoke for physical service separation (non-destructive to database)

In a disposable local Compose environment, after running the scripts, temporarily stop the Inventory HTTP service:

```powershell
docker compose stop inventory-api
# A Booking GET /events/{eventId}/seats should now return HTTP 503.
docker compose up -d inventory-api
```

That checks routing only: it is **not** evidence that the database is independently owned. Do not leave the Inventory API stopped when testing checkout.

## Design choices

- `Inventory:BaseUrl` is set in Compose to enable the HTTP adapter. When omitted, the Booking API retains its in-process `IInventoryService`, allowing existing Phase 1 contract tests and local standalone development to keep working until the legacy Booking API is retired.
- Booking's `/orders` route continues to prepare Inventory mutations in its local transaction; it must be disabled/retired before a physical Inventory database migration. Do **not** redirect the legacy checkout to another database.
- Authorization is verified by both Booking and Inventory with the same Keycloak issuer/audience. The Inventory API has no publicly bound host port in Compose.
- RabbitMQ messages, Ordering saga, payment/void/notification events, persistent outbox, and their schemas are unchanged.

## Next milestone — Phase 2.3o.2b

1. Remove the legacy synchronous checkout endpoint and its shared BookingDbContext write path, replacing it with the already-working `/checkouts` saga on Ordering API.
2. Establish an Inventory-owned DbContext and migration history for seats, holds, inbox, outbox and sale ledger; remove Catalog/legacy `orders` ownership from the new Inventory model.
3. Plan the **data migration** for current holds and sold seats and the cutover of Inventory API, Inventory command consumer and publisher to one new Inventory PostgreSQL database. Stop all old writers before exporting; ensure backfill includes no in-flight commands. Preserve uniqueness and sold states, and compare source/destination counts/checksums before enabling new writers.
4. Add cross-database isolation/upgrade tests and update scripts that inspect Inventory to query its new database rather than `postgres`.

## Proposed Git commit message (use AFTER verification)

```text
feat(inventory): extract Inventory HTTP API boundary (Phase 2.3o.2a)

- Add independently hosted Inventory API for seats, holds, and buyer-scoped reads
- Forward authenticated Booking API requests through an HTTP Inventory adapter
- Preserve hold-proof issuance and existing Ordering checkout contracts
- Retain legacy synchronous checkout transaction during the database transition
- Add Inventory API HTTP tests, Compose wiring, and bridge smoke verification

Prepare Inventory for independent persistence without moving authoritative data yet.
```

**Validation status:** generated patch; .NET/Testcontainers/Docker are unavailable in this runtime. No local tests have been run by the assistant.
