# Phase 2 — incremental implementation plan

Original Copilot goal: **extract independently deployable services, RabbitMQ, transactional outbox/inbox, durable checkout saga, Payment and Notification, and Aspire**. Keep the Phase 1 `no oversell` invariant while removing the cross-module database transaction.

## Stage 2.1 — groundwork (validated)

- [x] Write ADR for service-owned persistence and distributed checkout behavior
- [x] Introduce a `TicketRush.BuildingBlocks.Contracts` project with transport-neutral `V1` message records
- [x] Add local JSON serialization/dependency tests for message contracts (not yet broker interoperability tests)
- [x] Developer reported all **29** tests passing and pushed code to GitHub on October 20, 2026
- [x] Phase 1 Docker Compose HTTP smoke reported PASS on October 20, 2026

**Stage 2.1 had no runtime messaging, no outbox, and no microservice deployment.** It intentionally leaves `BookingService` removed and the working Phase 1 `OrderingService` + `InventoryService` intact.

**Validation limitation:** the sample JSON test configures strict required-constructor-parameter handling and web-style camelCase. This is not automatically applied to future MassTransit, RabbitMQ or other message consumers; configure and test the chosen transport explicitly. Validate GUIDs, reason codes and business state after deserialization.

## Stage 2.2 — first independent service

- [x] Independent Catalog HTTP host and persistence; user reported 32 passing tests, deployed services and Docker HTTP smoke PASS
- [x] Catalog owns its own CatalogEvent EF entity; Booking is configured to read EventSummary over HTTP without a cross-service DbContext reference
- [x] Compose and Testcontainers code added for independently bootstrapped Catalog (pending local execution)

## Stage 2.3 — establish durable ordering

- [ ] Verify Step 2.3a Ordering-owned DB, Pending/Completed/Failed data model, and unique idempotency key (patch prepared; local build/tests pending); not yet wired to public API
- [ ] Verify Step 2.3a atomic persistence of CheckoutRequestedV1 with the Pending intent (patch prepared; local DB test pending)
- [ ] Add RabbitMQ dispatcher with publish confirms, retry/ack, and consumer inbox deduplication
- [ ] Verify kill/restart around database commit vs. broker publish leaves no lost checkout intent

## Stage 2.4 — distributed checkout

- [ ] Inventory-owned DB, independent hold/sale idempotency and expiry validation
- [ ] Durable saga state with optimistic concurrency (one workflow per OrderId)
- [ ] Payment simulator with currency/amount policy, capture/refund idempotency and failure injection
- [ ] Saga scenarios: success, rejection, timeout, redelivery, out-of-order replies, forced Payment restart
- [ ] Notification consumer, noncritical to order completion

## Stage 2.5 — operability and exit

- [ ] Aspire for local orchestration; containerized dependencies through Compose
- [ ] Health/readiness, OpenTelemetry traces and structured metrics
- [ ] Document dead-letter inspection/replay and recovery runbook
- [ ] Demonstrate no duplicate charges, orders, or sold seats despite worker/broker restarts

Note: MassTransit v9 uses commercial licensing; compare supported v8 against available Apache/MIT alternatives before adding a package. Do not silently adopt v9. See `docs/adr/0007-phase2-service-ownership-and-checkout.md`.


### Phase 2.3o.1 — Inventory-owned sale ledger (transitional)

The async sale command now persists Inventory sale evidence in `inventory_sales`
instead of a duplicate legacy `TicketOrder` row. This is a preparatory ownership
change **inside the existing shared Booking database**, not a separate Inventory
DB yet. `README-STEP2.3O1-APPLY.md` covers migration, validation and the next cutover.

### Phase 2.3o.2a — Inventory HTTP boundary (validated by developer, October 22, 2026)

- [ ] Build/test `TicketRush.Inventory.Api` and the Booking HTTP adapter.
- [ ] Validate cross-service JWT identity and hold proof on the Booking API.
- [ ] Run `Verify-Inventory-Api-Bridge.ps1` and all downstream saga/compensation/notification regressions.
- [ ] Confirm Booking API read/hold requests return 503 during an Inventory API outage, not locally persisted responses.

**Only a service boundary has moved:** database tables, Inventory RabbitMQ workers, and
legacy synchronous `/orders` still share Booking PostgreSQL. The physical
Inventory database extraction is Phase 2.3o.2b, **not complete**.


### Phase 2.3o.2b — Retire legacy Booking checkout (awaiting local validation)

- [ ] Normal gateway does not register or receive BookingDbContext/database credentials.
- [ ] Legacy `/orders` route returns 410 Gone; signed-proof asynchronous Ordering `/checkouts` remains the supported path.
- [ ] Existing Phase 1 HTTP tests opt into explicit Development compatibility mode.
- [ ] Build/tests and gateway retirement, Inventory bridge, saga, payment-void, notification smoke scripts pass.

**Not yet a DB split:** Inventory API and workers continue to use the shared Booking DB. Physical ownership and historical-data migration are scheduled for Phase 2.3o.2c.
