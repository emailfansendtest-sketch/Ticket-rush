# Phase 1 completion validation — local modular monolith

This is an **acceptance checklist**, not a claim that the patch author executed tests or Docker. The developer reported **24 passing tests** before this patch. The two new expiry tests bring the expected count to **26**, pending local execution.

## Contract and correctness changes in this patch

- `GET /events/{eventId}/seats` reports expired Held seats as **Available** and omits the expired `heldUntilUtc` timestamp. The query is an `AsNoTracking` projection and does not mutate PostgreSQL rows. `HoldAsync` still reclaims expired seats inside its existing transaction.
- `GET /holds/{holdId}?buyerId={buyerId}` resolves a successful hold `Location`, with 404 when the buyer ID doesn't match; the response reports logical **Expired** if its Active hold is past expiry. An idempotent hold POST replay reports the same logical Expired status, even after the seat is reclaimed.
- `GET /orders/{orderId}?buyerId={buyerId}` resolves a successful order `Location`, also filtering by buyer.
- `POST` 201 `Location` headers now include `buyerId` to make them directly followable. **This changes the Location header format**, so clients that compare these headers as strings must update. Existing POST routes and response bodies are unchanged.
- **WARNING**: buyer IDs are supplied by callers. Buyer filtering is **not authentication or authorization**. Query parameters may appear in logs. These read endpoints must remain local-only until Phase 2 establishes authenticated identity.

## Commands (from solution root)

```powershell
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx -c Release
dotnet test .\TicketRush.slnx -c Release --no-build
# Expected total after this patch: 26
```

Testcontainers requires Docker Desktop/Engine. Two new tests: `Expired_seat_is_logically_available_at_expiry_boundary_without_db_mutation`, `Http_expired_hold_is_reported_available_and_can_be_reclaimed`. HTTP contract tests also follow the hold/order Location and verify missing/wrong buyer IDs.

## Real Compose deployment validation

```powershell
docker compose up -d --build
docker compose ps
docker compose logs migrate
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
```

Expected: PostgreSQL healthy, migration job exits successfully, API accepts requests. Compose publishes the unauthenticated API (port 8088) and development PostgreSQL (port 5439) on **127.0.0.1 only**, rather than exposing them to the host's other network interfaces. The smoke script picks **two currently available** demo seats (requires a seeded demo event), creates a hold, follows its Location, repeats the idempotent POST, confirms checkout, follows its Location, repeats checkout, verifies seats Sold, and expects 409 when another buyer tries a sold seat. No database reset is required. If all demo seats are already sold, use a fresh **disposable** database or seed a new event instead of destroying valuable data.

## Concurrency evidence

The existing integration suite includes **1,000 competing hold attempts for 100 seats**, with a concurrency limiter of **64**. Document this as *1,000 attempts / max 64 in flight*, not 1,000 simultaneously open database operations. Its invariant is 100 successful bookings and no double-sale under the defined setup. The suite additionally checks overlaps, competing idempotency keys, failed-checkout rollback and expired hold reclaim.

Capture the test run with a Git commit hash, machine environment, Docker/PostgreSQL versions and a command transcript. Do not claim throughput, latency percentiles, multiple replicas, payment reliability or production readiness based on these tests.

## Phase 1 exit

- [ ] Full Release build and expected tests pass locally.
- [ ] Compose migration + API startup + HTTP smoke pass locally.
- [ ] Concurrency evidence is recorded accurately (1,000 attempts; at most 64 concurrent).
- [ ] Commit Phase 1 completion code/docs and record the validated Git hash.

The shared PostgreSQL context and atomic Inventory–Ordering transaction remain **intentional**. Their separation into independent services with outbox/saga is **Phase 2**, not a Phase 1 prerequisite.
