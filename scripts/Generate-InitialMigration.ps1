# Run from TicketRush solution root on .NET 10 SDK machine (VS 2026 Developer PowerShell).
$ErrorActionPreference = 'Stop'
if (-not (Test-Path './TicketRush.slnx')) { throw 'Run from solution root.' }
if (Test-Path './src/TicketRush.Infrastructure/Migrations') {
    throw 'Migrations directory already exists. Do not regenerate or erase migration history.'
}
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }
dotnet restore ./TicketRush.slnx
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
dotnet ef migrations add InitialCreate `
    --project ./src/TicketRush.Infrastructure `
    --startup-project ./src/TicketRush.Api `
    --context BookingDbContext `
    --output-dir Migrations
if ($LASTEXITCODE -ne 0) { throw 'Generating InitialCreate failed.' }
dotnet ef migrations has-pending-model-changes `
    --project ./src/TicketRush.Infrastructure `
    --startup-project ./src/TicketRush.Api `
    --context BookingDbContext
if ($LASTEXITCODE -ne 0) { throw 'EF model differs from migration snapshot.' }
Write-Host 'Initial migration scaffolded. Review generated Up/Down and snapshot before applying.'
