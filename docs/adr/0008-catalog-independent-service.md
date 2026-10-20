# ADR 0008 — Extract Catalog into an independently hosted service

Status: **Implementation ready for local validation** (Phase 2 step 2.2)

## Decision

The Catalog service owns a distinct PostgreSQL database and a Catalog-only EF Core
`CatalogDbContext` and migration. `TicketRush.Catalog.Api` serves `GET /events`;
`TicketRush.Api` forwards its existing `GET /events` through the typed
`HttpEventCatalog` client. Catalog's EF entity (`CatalogEvent`) is not the Booking
entity (`TicketEvent`), and Catalog Storage does not reference TicketRush.Domain or
TicketRush.Infrastructure. Catalog DB updates never depend on booking transactions.

During the **migration period**, the Booking database *still* has a local `events`
reference table because seats and holds have foreign keys to it. The Booking-side
`DemoDataSeeder` remains to support its original booking demo. This is **not a
second Catalog source of truth**: the public event listing is served only from the
Catalog database, while Inventory uses its local event IDs for integrity checks.

**Known transitional limitation:** New events created only in Catalog will not be
bookable until Inventory receives their IDs and initializes seats. No event-management
API, replication or provisioning event stream is implemented yet. We must introduce
an explicit Catalog→Inventory provisioning contract/inbox and then migrate the local
Booking `events` registry and cross-owner foreign keys *before* declaring Inventory
independently deployable. Do not drop the booking FK tables now: doing so would
invalidate earlier migrations and Phase 1 tests.

This step uses two PostgreSQL containers (one per database owner) locally and a
separate migration job per database. Never call EF `Migrate()` from every API
replica. `GET /events` is an HTTP dependency: if Catalog is down, the gateway
currently returns an error; timeout/circuit-breaker and optional cache are later
hardening tasks, not claims of this step. Services use no auth yet, so Compose only
publishes development endpoints on loopback.

## Scope checks

- Build and run all integration tests, including `CatalogServiceHttpTests`.
- `GET http://localhost:8090/events` and `GET http://localhost:8088/events`
  return matching Catalog data; `GET /events` on Booking does not query BookingDb.
- Booking/Inventory/Ordering transactions and migrations have **not** changed.
- Existing initialized Booking volume remains valid; Catalog creates a new volume.
