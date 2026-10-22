# Confirm runtime services use Inventory-owned DB and the new schema excludes legacy orders.
$ErrorActionPreference = 'Stop'
function Require([bool]$ok, [string]$why) { if (-not $ok) { throw "Inventory database separation FAILED: $why" } }

foreach ($service in @('inventory-api', 'inventory-consumer', 'inventory-result-outbox')) {
    $container = (& docker compose ps -q $service) -join ''
    Require ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($container)) "$service container not running"
    $environment = (& docker inspect --format '{{range .Config.Env}}{{println .}}{{end}}' $container) -join "`n"
    Require ($LASTEXITCODE -eq 0) "could not inspect $service"
    Require ([bool]($environment | Select-String -SimpleMatch 'ConnectionStrings__InventoryDb=Host=inventory-postgres;')) "$service not using InventoryDb"
    Require (-not [bool]($environment | Select-String -SimpleMatch 'ConnectionStrings__BookingDb=')) "$service still has legacy BookingDb credentials"
}

$sql = @'
SELECT CASE
  WHEN to_regclass('public.events') IS NOT NULL
   AND to_regclass('public.holds') IS NOT NULL
   AND to_regclass('public.seats') IS NOT NULL
   AND to_regclass('public.inventory_sales') IS NOT NULL
   AND to_regclass('public.inventory_inbox') IS NOT NULL
   AND to_regclass('public.inventory_result_outbox') IS NOT NULL
   AND to_regclass('public.orders') IS NULL
  THEN 'OK' ELSE 'FAIL' END;
'@
$result = (& docker compose exec -T inventory-postgres psql -X -U ticketrush -d ticketrush_inventory -At -v ON_ERROR_STOP=1 -c $sql) -join "`n"
Require ($LASTEXITCODE -eq 0 -and $result.Trim() -eq 'OK') "Inventory schema is missing tables or includes legacy orders: $result"
Write-Host 'Inventory database separation smoke: PASS' -ForegroundColor Green
