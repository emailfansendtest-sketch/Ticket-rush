# TicketRush Phase 2.3r — static repository review

**Reviewed:** user-provided `Ticket-rush-master(2.3r).zip`, 23 October 2026. This is **static source and documentation inspection** only: no .NET SDK, Docker engine, Keycloak or PostgreSQL runtime was available to re-execute builds/tests.

## Highest-priority findings

1. **Critical documentation drift (`README.md`):** top-level status was incorrectly marked Phase 2.3b with validation pending; Phase 2.3r has been reported validated. Several old paragraphs said Keycloak, RabbitMQ, saga, Payment, and independent Inventory data ownership did not exist, despite current code/Compose wiring. **Fixed in documentation patch.**
2. **Broken fresh-start instructions (`README.md`):** `compose.yaml` requires `${HOLD_PROOF_KEY_BASE64:?...}` to be set, but README did not run `scripts/Initialize-Dev-Hold-Proof.ps1`. Inventory migrator has `SEED_DEMO_DATA: "false"`; fresh Inventory is empty unless explicitly seeded. **Fixed in README.**
3. **Obsolete checkout instructions (`README.md`):** sample `POST /orders` calls contradict `TicketRush.Api/Endpoints/OrderingEndpoints.cs`, which returns 410 Gone in normal mode. `scripts/Verify-Phase1-Docker.ps1` still requires 201 from `/orders`, therefore should not be run as a normal deployment smoke test. **Fixed in docs; historical script retained.**
4. **Accidental database backup exposure risk (`.gitignore`, `.dockerignore`):** cutover runbook recommends `backups/` inside repo, but that directory was not ignored by Git or excluded from Docker build context. Database dumps could be committed or copied into image-build contexts. **Patched both ignore files.**
5. **Outdated source-of-truth docs (`docs/roadmap/README.md`, `docs/phase2-work-plan.md`, `docs/validation.md`):** still showed Phase 2 tasks as pending and historic test counts as current. **Updated with current, qualified Phase 2.3r status.**

## Confirmed wiring / architectural observations

- `TicketRush.slnx` references every one of the repository's **29** `.csproj` files. No missing project references at the solution level.
- Compose config has **six PostgreSQL containers/databases**, including the legacy Booking database; Inventory, Ordering, Payment, Catalog and Notification use their own DBs in the normal deployment.
- Booking gateway uses `HttpInventoryService` and `HttpEventCatalog` and does not receive a `BookingDb` connection string in the default Compose service definition. `LegacyBooking:Enabled` is false.
- Inventory API, command consumer and result publisher receive `ConnectionStrings__InventoryDb`. The independent Inventory migrator is configured not to seed by default.
- Ordering exposes authenticated `POST /checkouts`, `GET /checkouts/{orderId}`, returning 202 for first accepted command. Broker handling is custom RabbitMQ.Client code, not MassTransit.
- Keycloak JWT buyer identification and signed hold-proof checking exist for the authenticated checkout path.
- Order terminal notifications produce simulated Notification receipts; no external email, SMS or payment provider is called.
- Local failure scripts for consumer outage, RabbitMQ outage and checkout replay exist. They are disruptive and should not be part of a non-disruptive default test command.

## Remaining issues to schedule separately

- **Health/readiness:** HTTP hosts expose `/health/live` but use `AddHealthChecks()` with no registered dependency checks; this is not a readiness endpoint proving RabbitMQ, Postgres, Catalog or Inventory availability. Add well-scoped readiness checks before Kubernetes work.
- **No CI workflow:** this snapshot has no `.github/workflows` files. For portfolio presentation, add PR build + reproducible integration tests (Docker-capable runner) once dependable.
- **No measured Phase 3 load/performance baseline:** no `TicketRush.LoadRunner` project exists in the uploaded Phase 2.3r snapshot. Do not claim latency, throughput, GC or multi-replica evidence yet.
- **Historical files at root:** many patch-specific `README-STEP*.md` and patch manifests are retained. They provide useful traceability, but risk confusing reviewers. Consider a later documentation organization commit moving them into `docs/history/` with link updates.
- **Data-copy verification scope:** the cutover snapshot compares counts and selected distributions, not full row-by-row checksums or a formal restore test. The runbook correctly warns against using the old database after post-cutover writes.

## What was *not* verified

- No local compilation or `dotnet test` re-execution.
- No `docker compose config` execution; Compose YAML inspected/parsed statically.
- No security penetration test, exhaustive transactional state-space analysis or proof of horizontal scalability.
- No comparison against the newest GitHub commit outside the supplied archive.

## Recommended action

Apply the accompanying documentation/hygiene patch on top of the reported validated Phase 2.3r tree. It contains no C# or database changes. Review the rendered README on GitHub, run `dotnet build`/`dotnet test` as a final check, and commit it separately as documentation/hygiene. Then apply Phase 3a performance work, which is **not included** in this source snapshot.
