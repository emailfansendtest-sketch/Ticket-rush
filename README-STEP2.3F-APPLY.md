# TicketRush Phase 2.3f — independent Ordering HTTP API

Apply the Step 2.3f patch over your **committed, tested Step 2.3e** repository (including the RabbitMQ 4.x probe-queue fix). Don't replace local user changes blindly.

## Scope

- Adds `TicketRush.Ordering.Api`, independent web service bound to `127.0.0.1:8091` in Compose.
- Exposes `POST /checkouts` (202 new, 200 replay, 400 invalid, 409 conflict) and `GET /checkouts/{orderId}?buyerId=...` (200, 400, 404).
- Uses `CheckoutIntakeService` to commit the Ordering intent + fact together; uses `CheckoutStatusReader` for current terminal state and reason.
- No Booking database dependency, no new migrations, and no changes to `/orders` on port 8088.
- Six additional Testcontainers/PostgreSQL HTTP tests; expected **71** total.
- Adds `Verify-Checkout-Api-Saga.ps1`; tests real POST and GET, broker processing, both databases, and idempotent replay. Uses one demo seat.

## Local validation

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
# Expected: 71 tests if all six new test cases pass.

dotnet ef migrations has-pending-model-changes `
    --project .\src\TicketRush.Ordering.Persistence `
    --startup-project .\src\TicketRush.Ordering.Persistence `
    --context OrderingDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs ordering-api ordering-migrate ordering-saga ordering-outbox

powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
```

Expected script output: `Ordering HTTP checkout saga smoke: PASS`. The script uses the seeded demo event and **sells one available seat**.

You can also run the previously validated Phase 2.3e saga and Inventory smoke scripts to check regressions.

## Examples

```powershell
$buyer = [guid]::NewGuid().ToString('D')
$holdId = '<a-real-hold-id-from-the-Booking-API>'
$created = Invoke-WebRequest -UseBasicParsing -Method Post -Uri 'http://localhost:8091/checkouts' `
    -ContentType 'application/json' -Body (@{ holdId = $holdId; buyerId = $buyer; idempotencyKey = 'demo-1' } | ConvertTo-Json)
$created.StatusCode # 202
$created.Headers['Location']
```

Read status from that Location on port 8091 until Completed/Failed; acceptance does not guarantee an Inventory sale.

## Deliberate limitations

1. The API runs separately from the legacy Booking API; no API gateway or proxy introduced in this increment.
2. No payment simulator, notification service, or compensation yet.
3. `buyerId` is an unverified identity supplied by the caller, **not authorization**. Docker binds locally; do not deploy publicly.
4. Pending orders may remain Pending indefinitely when infrastructure is down or a command is poison. Durable timeout/recovery comes next.
5. .NET build, database migrations and RabbitMQ smoke must be validated locally; this archive was statically inspected but not executed in the authoring container.
