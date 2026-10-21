# Phase 2.3i: Signed hold proof at checkout admission

Apply this patch **over validated Phase 2.3h**. This increment is deliberately scoped to
prevent a caller who only knows another buyer's `holdId` from reserving that hold in
Ordering's unique `order_intents` table. It does **not** introduce an identity provider.

## New behavior

- Booking `POST /events/{eventId}/holds` replies with a `holdProof` alongside the
  existing hold fields, both for a new hold and an idempotent hold replay.
- Booking `GET /holds/{id}` never discloses `holdProof`.
- Ordering `POST /checkouts` now requires `holdProof` in the JSON body. It checks an
  HMAC-SHA256 signature binding the hold ID, buyer ID and hold expiration before
  calling `CheckoutIntakeService`. Invalid/expired/missing proofs return HTTP 403;
  no `order_intents` or `outbox_messages` rows are inserted.
- The proven synchronous Booking `POST /orders`, RabbitMQ message contracts, and
  Inventory sale processing are unchanged.
- Status GET still uses a caller-supplied `buyerId`; it remains **unauthenticated**,
  and must stay on localhost. A future identity-provider milestone must authorize
  both hold creation and checkout status retrieval.

## Development secret

Both APIs need the identical `HoldProof:KeyBase64` value, decoded to at least 32
bytes. **No key is embedded in Compose or the repository.** Before using Compose,
run `powershell -ExecutionPolicy Bypass -File .\scripts\Initialize-Dev-Hold-Proof.ps1`
from the solution root. The script securely generates a random key and saves it
in `.env` (already ignored by Git). Compose will refuse to start without it.
You can alternatively supply `HOLD_PROOF_KEY_BASE64` through the environment or
a secret manager. Never commit `.env`; do not log or expose the key. Use proper
key rotation and secret management for non-demo deployments.

When running both APIs directly in Visual Studio rather than Docker, configure
`HoldProof__KeyBase64` for **each** API process with the same secret (for example,
via user secrets or launch environment configuration, not source control).

## Validation

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Initialize-Dev-Hold-Proof.ps1
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
docker compose up -d --build
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Recovery.ps1
```

The existing HTTP-to-saga smoke test now passes the Booking-issued hold proof.
It consumes one demo seat. Recovery smoke tests do not initiate HTTP checkouts.
No migration is required.

## Limitations

A signed hold receipt is a **bearer capability**, not an authenticated buyer identity:
whoever obtains a valid receipt can act on that hold. Booking's existing hold creation
also trusts the supplied buyer ID. Local keys must not be checked into source control. Real OIDC/JWT authentication, authorization,
TLS, proper broker/database credentials, and capability-key rotation remain future
production-hardening tasks. Furthermore, the check occurs before Ordering persists
its intent; Inventory remains authoritative for hold state at sale time.
