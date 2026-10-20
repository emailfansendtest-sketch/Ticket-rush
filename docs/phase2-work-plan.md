# Phase 2 — incremental implementation plan

Original Copilot goal: **extract independently deployable services, RabbitMQ, transactional outbox/inbox, durable checkout saga, Payment and Notification, and Aspire**. Keep the Phase 1 `no oversell` invariant while removing the cross-module database transaction.

## Stage 2.1 — groundwork (this patch)

- [x] Write ADR for service-owned persistence and distributed checkout behavior
- [x] Introduce a `TicketRush.BuildingBlocks.Contracts` project with transport-neutral `V1` message records
- [x] Add local JSON serialization/dependency tests for message contracts (not yet broker interoperability tests)
- [ ] Validate project build and all **29** tests locally (26 reported passing before this patch)
- [ ] Record the Phase 1 Docker Compose smoke result, which has not yet been reported

**This patch has no runtime messaging, no outbox, and no microservice deployment.** It intentionally leaves `BookingService` removed and the working Phase 1 `OrderingService` + `InventoryService` intact.

**Validation limitation:** the sample JSON test configures strict required-constructor-parameter handling and web-style camelCase. This is not automatically applied to future MassTransit, RabbitMQ or other message consumers; configure and test the chosen transport explicitly. Validate GUIDs, reason codes and business state after deserialization.

## Stage 2.2 — first independent service

- [ ] Separate Catalog HTTP host and Catalog-owned persistence/migrations (keep GET /events behavior); plan the existing `holds`/`seats` foreign keys to `events` and event-seeding transition
- [ ] Stop sharing EF/domain entities across service host boundaries; publish event definitions through contracts or APIs
- [ ] Add Compose and Testcontainers coverage for independently bootstrapped Catalog

## Stage 2.3 — establish durable ordering

- [ ] Ordering has its own order DB, new Pending/Completed/Failed state, and unique idempotency key
- [ ] Configure transactional outbox using a pinned, license-reviewed messaging library; add RabbitMQ, retry/ack and inbox
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
