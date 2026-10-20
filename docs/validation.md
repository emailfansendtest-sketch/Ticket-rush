# Phase 1 validation checklist

**Developer-verified baseline (October 20, 2026):** Catalog, Inventory and Ordering modules build in Visual Studio 2026, and all **24** tests (including initial HTTP tests) passed. This completion patch adds two expiry tests and updates HTTP Location verification; the developer has **not yet confirmed** the new code passes locally.

## Run this patch

```powershell
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx -c Release
dotnet test .\TicketRush.slnx -c Release --no-build
docker compose up --build
```

**Expected:** 26 tests if the two new expiry tests pass. The API tests use PostgreSQL Testcontainers and ASP.NET Core TestServer. The `Microsoft.AspNetCore.Mvc.Testing` package remains centrally pinned to `10.0.13`. Run `scripts/Verify-Phase1-Docker.ps1` after Compose starts and see `docs/phase1-completion.md`.

## Verified by the previously passing service/database tests

- [x] Competition for seats, including 1,000 hold attempts on 100 seats
- [x] Multi-seat requests are all-or-nothing
- [x] Same-buyer and different-buyer idempotency-key semantics
- [x] Concurrent duplicate checkout cannot duplicate orders
- [x] An expired hold can be reclaimed and cannot later be confirmed by its former buyer
- [x] Different holds racing on one checkout key leave the loser unmodified
- [x] Inventory checkout preparation remains inside Ordering's transaction
- [x] EF migrations and demo seed initialization work in Testcontainers

## HTTP tests (two previously verified; expiry and Location corrections pending)

- [x] `GET /events`, `GET /events/{id}/seats`, `POST /events/{id}/holds`, `POST /orders`, `GET /health/live` respond as expected
- [x] HTTP `201` versus `200` idempotent replays, `409` seat conflict, `400` invalid request, `404` missing seat
- [x] HTTP checkout persists one order and final seat state in PostgreSQL
- [ ] Location headers resolve to hold/order GET endpoints, with buyer filtering and 400/404 handling
- [ ] Expired held seats are read as Available without modifying persisted seat rows

## Remaining limitations and next phase

- Buyer IDs are supplied by the caller: **do not expose this demo API publicly**. Compose binds the API and PostgreSQL host ports to `127.0.0.1` only. Authentication/authorization is Phase 2.
- Expired-hold POST replays and GET resource reads now both report logical `Expired` status, even after the seat is reclaimed.
- Payments, RabbitMQ, inbox/outbox, sagas and per-service persistence are **not** part of Phase 1.
- Current modules share one EF Core DbContext and transaction. This is intentional and must not be mistaken for isolation between separately deployed services.
- No claims about throughput, p95/p99, memory allocation, or three-replica scale-out until measured in Phases 3–4.
- A request-size policy, structured operational logging, and readiness checks remain worthwhile follow-up hardening; do not infer their existence from the passing tests.
