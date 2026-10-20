# Optional cleanup after extracting the Step 2.2 patch at solution root.
$ErrorActionPreference = 'Stop'
$obsolete = Join-Path $PSScriptRoot '..\src\TicketRush.Catalog\EfEventCatalog.cs'
if (Test-Path $obsolete) {
    Remove-Item -LiteralPath $obsolete -Force
    Write-Host "Removed obsolete Phase 1 Catalog EF adapter: $obsolete"
}
