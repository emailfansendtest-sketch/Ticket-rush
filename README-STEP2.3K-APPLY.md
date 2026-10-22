# Phase 2.3k — Independent Simulated Payment Service

Baseline: **validated Phase 2.3j** (92 tests and three working smoke tests; fix to
`TestBuyerAuthentication.Scheme` included in the complete archive).

This is an ISOLATED PAYMENT SANDBOX. It does **not** alter Ordering's saga,
Booking/Inventory seat sales, checkout HTTP responses, or charge real funds.
There is no trusted price calculation yet, therefore `AmountMinor` in a Payment
command is only a synthetic test input. `SimulateDecline` is an explicit
sandbox-only control and must not be used for real payments.

- Independent `payment-postgres` container and `TicketRush.Payment.Persistence` EF Core migration.
- `TicketRush.Payment.DbMigrator` applies the schema.
- `TicketRush.Payment.Worker` consumes durable `PaymentAuthorizationRequestedV1`
  messages, stores a unique receipt per command and order, and atomically creates
  `PaymentAuthorizedV1` / `PaymentDeclinedV1` outbox records.
- An independent publisher in the same worker delivers results with tracked RabbitMQ
  confirmations to a durable queue, `ticket.ordering.payment-results.v1`.
  No Ordering consumer is attached to this queue in 2.3k.
- Replayed commands preserve the original result MessageId and can re-arm a previously
  published result. No second authorization record is created.

## Validate

```powershell
# With your existing ignored .env signing key in place:
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build

dotnet ef migrations has-pending-model-changes `
  --project src/TicketRush.Payment.Persistence `
  --startup-project src/TicketRush.Payment.Persistence `
  --context PaymentDbContext

docker compose up -d --build
docker compose ps -a
docker compose logs payment-migrate payment-worker
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Simulation.ps1
```

Expected test count: **100** (92 existing + 8 new tests, counting the two-case theory).

The Payment smoke publishes two synthetic AMQP commands, then observes both
Payment outcomes in a temporary RabbitMQ queue and confirms Payment outbox
publication in PostgreSQL. It never creates a Booking hold or moves an Ordering
state; no real money or seats are affected.

## Deferred to Phase 2.3l

Order-side trusted pricing and Payment command scheduling; sequencing against
Inventory's currently terminal seat sale; compensation or release semantics;
Payment result consumption in Ordering; stuck-payment recovery; a sandbox-only
payment failure path. Do not integrate Payment into a live checkout until these
invariants are explicitly designed and tested.
