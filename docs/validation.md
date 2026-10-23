# Verification guide — Phase 2.3r snapshot

This guide lists **current** validation entry points. Earlier scripts and individual patch README files may target prior stages. See [root README](../README.md) for the API, setup, and safety notes.

## Build and integration tests

```powershell
# Requires a running Docker engine for Testcontainers.
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
```

The user reported the suite passing after Phase 2.3r. Avoid fixed hardcoded test totals, because the suite evolves and theories may generate multiple cases.

## Normal deployment checks

```powershell
.\scripts\Verify-Inventory-Database-Separation.ps1
.\scripts\Verify-Legacy-Checkout-Retired.ps1
```

These establish important properties of the *running* Compose deployment but are not substitutes for transaction/idempotency integration tests.

## End-to-end checkout and compensation

```powershell
.\scripts\Verify-Checkout-Api-Saga.ps1
.\scripts\Verify-Payment-Void-Compensation.ps1
.\scripts\Verify-Notification-Service.ps1
```

These can consume demo seats; use only against the development deployment. The Payment and Notification integrations are simulations.

## Disruptive resilience drills (idle disposable development stack only)

```powershell
.\scripts\Verify-Inventory-Consumer-Outage-Recovery.ps1 -AllowDisruption
.\scripts\Verify-RabbitMq-Outage-Outbox-Recovery.ps1 -AllowDisruption
.\scripts\Verify-Checkout-Fact-Ambiguous-Ack-Replay.ps1 -AllowDisruption
```

Scripts temporarily stop components or deliberately replay a test event, and may leave data requiring manual investigation after a failure. Read their matching historical runbooks for preconditions and restoration behavior.

`Verify-Phase1-Docker.ps1` uses the retired synchronous `/orders` route and is not valid against the normal Phase 2.3r Compose configuration. Keep it as historical compatibility coverage rather than as the current smoke gate.

## Inventory migration

Fresh installs require explicit Inventory demo seeding if test seats are needed. Existing installs require the maintenance-window cutover and snapshot comparison. Read [Phase 2.3o.2c cutover](../README-STEP2.3O2C-APPLY.md); **do not reset Docker volumes** to fix a data migration problem.

## What has not been measured

No verified large-scale traffic, p95/p99 latency baseline, multi-replica scaling curve, or GC/allocation comparison is present in this Phase 2.3r snapshot. Those belong to the next project phases.
