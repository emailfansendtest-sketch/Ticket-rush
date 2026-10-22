# Phase 2.3o.2c: READ-ONLY validation of the maintenance-window Inventory copy.
# Run AFTER InventoryDataCopier commits, BEFORE restarting any writers/consumers.
# Never use this script as a validation of a live/dual-write deployment: the old
# database is expected to diverge once the new system accepts bookings.
$ErrorActionPreference = 'Stop'
function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "Inventory cutover copy FAILED: $message" }
}

# The copy must be validated while every component capable of writing Inventory
# or releasing work from upstream outboxes/sagas remains stopped.
$mustBeStopped = @(
    'api', 'inventory-api', 'inventory-consumer', 'inventory-result-outbox',
    'ordering-api', 'ordering-outbox', 'ordering-saga', 'payment-worker',
    'notification-worker'
)
$running = @(& docker compose ps --status running --services)
Require ($LASTEXITCODE -eq 0) 'could not determine running Compose services'
$unexpected = @($mustBeStopped | Where-Object { $running -contains $_ })
Require ($unexpected.Count -eq 0) ('writers/workers are running: ' + ($unexpected -join ', '))

function Invoke-Count([string]$service, [string]$database, [string]$sql) {
    $raw = @(& docker compose exec -T $service psql -X -U ticketrush -d $database -At -v ON_ERROR_STOP=1 -c $sql)
    Require ($LASTEXITCODE -eq 0) "SQL query failed on $service : $sql"
    $s = ($raw -join "`n").Trim()
    $parsed = 0L
    Require ([long]::TryParse($s, [ref]$parsed)) "unexpected database result on $service : $s"
    return $parsed
}

$sourceService = 'postgres'
$sourceDb = 'ticketrush'
$targetService = 'inventory-postgres'
$targetDb = 'ticketrush_inventory'

$targetHasLegacyOrders = Invoke-Count $targetService $targetDb "SELECT CASE WHEN to_regclass('public.orders') IS NULL THEN 0 ELSE 1 END;"
Require ($targetHasLegacyOrders -eq 0) 'target unexpectedly contains legacy orders table'

# Table-level data integrity check. This complements—not replaces—the copy
# tool's destination-transaction and per-table count verification.
$tables = @('events','holds','seats','inventory_sales','inventory_inbox','inventory_result_outbox')
$allCount = 0L
foreach ($table in $tables) {
    $sql = "SELECT COUNT(*) FROM public.$table;"
    $before = Invoke-Count $sourceService $sourceDb $sql
    $after = Invoke-Count $targetService $targetDb $sql
    Require ($before -eq $after) "count mismatch $table : source=$before destination=$after"
    $allCount += $before
    Write-Host ("{0,-26} source={1,7} target={2,7}" -f $table,$before,$after)
}

# Verify that important state dimensions also match; row counts alone cannot
# detect an incorrect seat status or changed outbox publication state.
$stateChecks = @(
    @{name='available seats'; sql='SELECT COUNT(*) FROM seats WHERE status=0;'},
    @{name='held seats'; sql='SELECT COUNT(*) FROM seats WHERE status=1;'},
    @{name='sold seats'; sql='SELECT COUNT(*) FROM seats WHERE status=2;'},
    @{name='confirmed holds'; sql='SELECT COUNT(*) FROM holds WHERE status=1;'},
    @{name='pending result events'; sql='SELECT COUNT(*) FROM inventory_result_outbox WHERE published_at_utc IS NULL;'},
    @{name='published result events'; sql='SELECT COUNT(*) FROM inventory_result_outbox WHERE published_at_utc IS NOT NULL;'},
    @{name='outbox publish attempt sum'; sql='SELECT COALESCE(SUM(publish_attempts),0) FROM inventory_result_outbox;'}
)
foreach ($item in $stateChecks) {
    $source = Invoke-Count $sourceService $sourceDb $item.sql
    $target = Invoke-Count $targetService $targetDb $item.sql
    Require ($source -eq $target) "$($item.name) mismatch: source=$source destination=$target"
}
if ($allCount -eq 0) {
    Write-Warning 'Source contains zero Inventory rows. Confirm this is an intentional empty deployment.'
}
Write-Host 'Inventory cutover copy snapshot: PASS' -ForegroundColor Green
Write-Host 'No data was changed. After restarting services, the two databases are expected to diverge.'
