# Phase 2.3o.2c — one-time Inventory database cutover
# Run only for an existing Booking database after maintenance shutdown and backups.
# This is NOT intended for a fresh deployment; never rerun after the destination
# has accepted Inventory writes.
[CmdletBinding()]
param(
    [switch]$BackupConfirmed,
    [switch]$ConfirmCopy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "Inventory data cutover FAILED: $message" }
}

function Check-ExitCode([string]$step) {
    Require ($LASTEXITCODE -eq 0) "$step failed (exit code $LASTEXITCODE)"
}

function Get-TableRows([string]$service, [string]$database) {
    # These six tables are the exact copied Inventory schema from Phase 2.3o.2c.
    $sql = @'
SELECT (SELECT COUNT(*) FROM public.events)
     + (SELECT COUNT(*) FROM public.holds)
     + (SELECT COUNT(*) FROM public.seats)
     + (SELECT COUNT(*) FROM public.inventory_sales)
     + (SELECT COUNT(*) FROM public.inventory_inbox)
     + (SELECT COUNT(*) FROM public.inventory_result_outbox);
'@
    $output = @(& docker compose exec -T $service psql -X -U ticketrush -d $database -At -v ON_ERROR_STOP=1 -c $sql)
    Check-ExitCode "Inventory table preflight on $service"
    $raw = ($output -join "`n").Trim()
    $rows = 0L
    Require ([long]::TryParse($raw, [ref]$rows)) "unexpected table-count response from $service : $raw"
    return $rows
}

Require ($BackupConfirmed.IsPresent) 'take and verify database backups, then specify -BackupConfirmed'
Require ($ConfirmCopy.IsPresent) 'read the cutover runbook, then specify -ConfirmCopy'
Require (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Verify-Inventory-Cutover-Copy.ps1')) 'missing scripts/Verify-Inventory-Cutover-Copy.ps1'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    # A full compose config check also verifies required environment variables.
    & docker compose config --quiet
    Check-ExitCode 'docker compose config'

    $mustBeStopped = @(
        'api', 'inventory-api', 'inventory-consumer', 'inventory-result-outbox',
        'ordering-api', 'ordering-outbox', 'ordering-saga', 'payment-worker',
        'notification-worker'
    )
    $running = @(& docker compose ps --status running --services)
    Check-ExitCode 'checking running Compose services'
    $unexpected = @($mustBeStopped | Where-Object { $running -contains $_ })
    Require ($unexpected.Count -eq 0) ('writers/workers still running: ' + ($unexpected -join ', '))
    foreach ($service in @('postgres', 'inventory-postgres')) {
        Require ($running -contains $service) "database service '$service' is not running"
    }

    $sourceRows = Get-TableRows 'postgres' 'ticketrush'
    $targetRows = Get-TableRows 'inventory-postgres' 'ticketrush_inventory'
    Require ($sourceRows -gt 0) 'source contains no Inventory rows; use the fresh-deployment path instead'
    Require ($targetRows -eq 0) "destination contains $targetRows Inventory rows; refusing to overwrite or recopy"
    Write-Host "Copying $sourceRows existing Inventory rows to the empty Inventory database..."

    # The C# cutover tool also checks the target, migration state, connection
    # identity, and executes its import in a single destination transaction.
    # Only this invocation temporarily sets the confirmation environment value.
    $oldConfirmation = [Environment]::GetEnvironmentVariable('INVENTORY_CUTOVER_CONFIRMED', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('INVENTORY_CUTOVER_CONFIRMED', 'YES', 'Process')
        & docker compose --profile cutover run --rm --no-deps inventory-cutover
        Check-ExitCode 'one-time Inventory database copy'
    }
    finally {
        [Environment]::SetEnvironmentVariable('INVENTORY_CUTOVER_CONFIRMED', $oldConfirmation, 'Process')
    }

    Write-Host 'Verifying the database snapshot while application writers remain stopped...'
    & (Join-Path $PSScriptRoot 'Verify-Inventory-Cutover-Copy.ps1')
    Require $? 'snapshot verification script failed'

    Write-Host 'Inventory data cutover and snapshot verification: PASS' -ForegroundColor Green
    Write-Host 'Next: resume services and run Inventory/Ordering/Payment/Notification smoke tests.'
    Write-Host 'Do not repeat this cutover after Inventory services start writing to the new database.'
}
finally {
    Pop-Location
}
