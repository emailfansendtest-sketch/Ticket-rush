# Ordering extraction patch — application and validation

Baseline: `TicketRush_Phase1_Inventory_Extraction_20Oct2026.zip`, which
you reported building successfully with all 17 tests passing.

## Apply

1. Commit or back up the current working tree.
2. Extract `TicketRush_Ordering_Extraction_Patch.zip` into the directory
   containing `TicketRush.slnx` (there is **no wrapper directory**).
3. **Delete** `src/TicketRush.Infrastructure/BookingService.cs`. ZIP overlay
   extraction does not remove older files automatically. The new
   `TicketRush.Ordering.OrderingService` replaces it. Do NOT delete any
   modified file if your local BookingService has diverged from the baseline;
   instead review the changes with Git first.
4. Run the checks below.

## Check

```powershell
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx --no-restore
dotnet test TicketRush.slnx --no-build
```

There are 5 new xUnit facts in `OrderingIntegrationTests`, so expect **22**
passing tests if the previous suite contains 17 tests.

The new tests verify exact Created/Replayed timestamp equality and rollback
of a competing hold when a shared order-idempotency key collides.

Verify `POST /orders` still returns 201 for the initial checkout, 200 for
the same idempotency key on retry, and 409 for a conflicting key.

## Intentional invariants

- Existing API route and JSON shape unchanged.
- Database migrations and test initializer unchanged.
- `Directory.Packages.props`, including cryptography security pins, unchanged.
- No RabbitMQ, Redis, second database, or cross-process transaction yet.
- Inventory's checkout port is only valid inside an Ordering-owned transaction.

See `docs/adr/0006-ordering-extraction.md` for architectural reasoning.
