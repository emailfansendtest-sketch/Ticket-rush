# Validation checklist — not yet executed

**Known limitation of this generated starter:** neither the .NET SDK nor Docker is installed in the artifact authoring environment. Source, packages and API wiring have NOT been built or executed. The user should use the commands below on a development PC before interpreting tests as verified.

```powershell
dotnet --version
docker version
dotnet restore .\TicketRush.slnx
dotnet build .\TicketRush.slnx -c Release
dotnet test .\TicketRush.slnx -c Release --no-build
docker compose up --build
```

**Critical before a public portfolio demo:** add EF migrations, replace exposed `buyerId` with Keycloak claims, add buyer authorization/tenant boundaries, validate request payload size, review race tests under contention, and record actual execution output. Review the compiler diagnostics of `FromSqlInterpolated` and `xmin` if provider versions change.

Test matrix to implement before Phase 1 can be considered finished:

- [ ] Competing buyers for a single seat (one success)
- [ ] 1,000 checkout attempts for 100 seats (100 orders, zero duplicates)
- [ ] Two buyers each request overlapping 3-seat sets (all-or-none)
- [ ] Concurrent duplicate holds with identical idempotency keys
- [ ] Concurrent checkout of same hold under different keys
- [ ] Expired hold reclaimed while an old checkout races
- [ ] Crash/rollback before transaction commit produces no partial sale
- [ ] Event exists but some requested seats do not (404, no partial mutation)
- [ ] Container start, reset and schema ownership remain deterministic
- [ ] Memory snapshot and load baseline established for later phases
