# Phase 2.3o.2b — Test-host DI / Minimal API binding correction

Apply **on top of** the Phase 2.3o.2b legacy checkout retirement patch (not directly on 2.3o.2a). This is a source-only fix; no database migrations, resets, or Compose changes.

## Root cause

A gateway without `Inventory:BaseUrl` and without early `LegacyBooking:Enabled` did not register `IInventoryService`. Minimal API consequently inferred the parameter as a request body, which is forbidden for GET endpoints and prevented the entire application from starting. Phase 1 test hosts also add their legacy-mode settings later than the application's initial service-registration logic.

## Changes

1. Always register an HTTP-backed `IInventoryService` in the normal gateway; resolve `Inventory:BaseUrl` from DI configuration when the client is instantiated, including test-host overrides. Without a URL, Inventory calls return HTTP 503; Catalog and retired checkout routes still work.
2. Mark all three Inventory endpoint `IInventoryService` parameters `[FromServices]`.
3. Read the *final* `LegacyBooking:Enabled` setting before mapping legacy vs retired order routes, with a Development-only guard.
4. Register `BookingDbContext`, Inventory and Ordering services explicitly in isolated Phase 1 compatibility test hosts. Normal gateway deployment still has no database credentials or DbContext registration.
5. Add a gateway-only regression test for missing Inventory configuration.

## Validate locally (Docker needed for Testcontainers)

```powershell
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
docker compose config --quiet
docker compose up -d --build
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Legacy-Checkout-Retired.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Api-Bridge.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Void-Compensation.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Notification-Service.ps1
```

If the full suite passes, run the remaining smoke scripts from your existing checklist.

## Suggested Git commit message (only after passing tests)

```text
fix(booking): restore inventory DI and legacy test-host compatibility (Phase 2.3o.2b)

- Register Inventory HTTP adapter regardless of optional gateway configuration
- Resolve Inventory URL after test-host configuration and return 503 if unconfigured
- Mark Inventory endpoint dependencies as explicit injected services
- Restore Phase 1 test-host database services without adding them to the normal gateway
- Select legacy checkout routes from final configuration and add startup regression coverage
```

**Status:** Source reviewed and structurally checked; .NET SDK/Docker are not available in the authoring environment. You must compile and test locally before committing.
