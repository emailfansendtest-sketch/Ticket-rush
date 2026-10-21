# Phase 2.3j — Buyer JWT authentication (Keycloak local development)

Apply this patch over the **committed, validated Phase 2.3i repository**, including the lazy HoldCheckoutProof DI fix. There are no database migrations.

## Scope

Booking protects POST holds, GET holds, POST synchronous orders and GET orders. Ordering protects POST checkouts and GET checkout status. Public event/seat lists and health endpoints remain anonymous. Authenticated buyer identity is the UUID in JWT `sub`. Caller-provided buyerId is checked against that identity; it never grants access by itself. A holdProof remains required for async checkout.

A real Keycloak development realm is imported with two fixed UUID user accounts `alice` and `bob`, and a restricted audience mapper. The test-host uses an isolated fake bearer scheme only inside integration tests. The smoke scripts obtain real JWTs from Keycloak using a **development-only** direct access grant. Do **not** deploy this realm or use its publicly known example passwords outside the localhost demo. Production should use HTTPS and Authorization Code + PKCE or another appropriate interactive login flow.

## Run

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Initialize-Dev-Hold-Proof.ps1
docker compose config --quiet
dotnet restore TicketRush.slnx
dotnet build TicketRush.slnx -c Release
dotnet test TicketRush.slnx -c Release --no-build
docker compose up -d --build
```

The realm uses the `oidc-audience-mapper` **custom audience** value
`ticketrush-apis`. A client-audience value is not valid unless that client exists
in Keycloak. The token helper validates `aud`, `iss`, and the expected demo
`sub` before returning a token (the APIs still validate the JWT signature).

**If Keycloak already ran with an older imported realm**, changing the JSON
file does not modify the running realm. Recreate the local Keycloak container
(no Keycloak data volume is configured in this demo), then let it import the
corrected realm:

```powershell
docker compose up -d --force-recreate keycloak
```

Wait for Keycloak to import the realm (the first startup can be slow):

```powershell
Invoke-RestMethod http://localhost:8092/realms/ticketrush/.well-known/openid-configuration
powershell -ExecutionPolicy Bypass -File .\scripts\Get-Dev-BuyerToken.ps1 -Username alice
```

Do not copy access tokens into GitHub or logs. Then execute:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Checkout-Api-Saga.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Ordering-Saga-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Inventory-Result-Recovery.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Verify-Phase1-Docker.ps1
```

The authenticated HTTP saga smoke consumes one demo seat, and the Phase 1 smoke consumes two. Recovery scripts use synthetic orders and do not sell seats.

**Docker network note:** the development Keycloak realm's issuer is `http://keycloak:8080/realms/ticketrush`. The host uses the mapped `http://localhost:8092` only to request tokens; the JWT `iss` remains the Docker-internal issuer. The Booking and Ordering APIs run in Compose and validate Keycloak JWTs over the internal network. To run either API directly in Visual Studio, set `Auth:Authority` to a reachable issuer and configure matching issuer URLs, rather than simply changing the host mapping.

**Migration:** None. **Test count:** previously 90; two additional authenticated-subject tests are included, so expect 92 passing tests, provided none have been added locally.

**Security limitations:** Keycloak `start-dev` uses HTTP and bundled development identities/credentials; Docker services remain bound to localhost. Do not promote this configuration to production. Reconfigure a real IdP, TLS, client flows, and secret storage for production.
