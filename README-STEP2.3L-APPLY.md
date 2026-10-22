# Phase 2.3l — Payment authorization gates Inventory checkout

Based on Phase 2.3k. Payment is an independent simulator with its own PostgreSQL DB.

## Business flow

For **new authenticated HTTP /checkouts**, `PaymentRequired = true`:

`CheckoutRequestedV1` -> Ordering saga -> `PaymentAuthorizationRequestedV1`
-> Payment worker (receipt + result outbox, one transaction)
-> `PaymentAuthorizedV1` or `PaymentDeclinedV1`
-> Ordering's durable payment inbox.

* On **decline**, Ordering becomes Failed with `SimulatedDecline`; **no Inventory command** is scheduled.
* On **authorization**, Ordering atomically schedules `InventorySaleRequestedV1`, remains Pending, and becomes Completed only after Inventory's sale-committed result. Inventory rejection still causes Failed.
* Historical/test intents with `payment_required=false` retain their old Inventory-only behavior. Those old intents must not be mistaken for paid checkouts.
* The watchdog now replays the original Payment command when there is not yet an Inventory command; Payment's inbox re-arms its original result, preserving the MessageId.

**Sandbox-only economics:** The Payment command uses a fixed server-chosen `1000 USD minor units` (USD 10.00) without referencing Catalog seat prices. This is a deterministic authorization simulator, not real pricing or charging. `simulatePaymentDecline` is supported only where `PaymentSandbox:AllowTestDeclines=true`; local Compose enables it for demo testing. No captured money or refund exists.

**Not yet implemented:** payment authorization void/release if Inventory subsequently rejects (no capture occurs, but a production authorization must be voided). This belongs to the compensation increment. The Inventory database is still transitional/shared with Booking; notifications and independent Inventory persistence are also future work.

## Apply and validate

Apply the patch over your **committed and validated Phase 2.3k solution**. The patch carries the audited Payment persistence error classification, including the additional regression test; your reported baseline contains 100 passing tests, whereas that audited archive may have one extra test. Do not infer an exact new test total from this difference.

Run:

```powershell
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
dotnet ef migrations has-pending-model-changes --project .\src\TicketRush.Ordering.Persistence --startup-project .\src\TicketRush.Ordering.Persistence --context OrderingDbContext
docker compose up -d --build
docker compose ps -a
```

Live probes:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Payment-Simulation.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Payment-Decline.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Recovery.ps1
```

The successful HTTP smoke now verifies one Payment authorization and one Inventory sale. The decline probe verifies no Inventory sale/command is created. Use disposable seeded demo data; the successful checkout sells one seat, and the declined checkout temporarily holds a seat until its hold expires.

**Known limitations:** This is local/dev infrastructure; Keycloak uses demo identities. The real JWT tests still depend on running Keycloak, whereas the in-process integration tests use the dedicated test auth handler. .NET / Docker were not available to the patch author; local compilation and runtime checks are acceptance requirements.
