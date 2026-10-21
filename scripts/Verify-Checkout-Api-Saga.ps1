# Phase 2.3f: HTTP checkout intake -> Ordering outbox -> saga -> Inventory -> result -> Ordering status.
# Requires disposable demo data. This script sells ONE demo seat and does not perform payment.
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091'
)
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Checkout HTTP saga smoke FAILED: $message" }
}
$health = Invoke-WebRequest -Uri "$OrderingUrl/health/live" -UseBasicParsing
Require ([int]$health.StatusCode -eq 200) 'Ordering API is not healthy'
$eventId = '11111111-1111-1111-1111-111111111111'
# Keep the raw Invoke-RestMethod JSON array: wrapping it in @() can cause
# nested-array enumeration issues in Windows PowerShell 5.1.
$seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
$seat = $seats | Where-Object {
    $_.status -eq 0 -or $_.status -eq 'Available'
} | Select-Object -First 1
Require ($null -ne $seat) 'no available demo seats remain'

$buyerId = [guid]::NewGuid().ToString('D')
$holdKey = 'api-saga-hold-' + [guid]::NewGuid().ToString('N')
$hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -ContentType 'application/json' -Body (@{
        buyerId = $buyerId; idempotencyKey = $holdKey;
        seatCodes = @($seat.seatCode)
    } | ConvertTo-Json -Depth 5)
Require ($null -ne $hold.id) 'could not create Booking hold'

$checkoutKey = 'api-saga-checkout-' + [guid]::NewGuid().ToString('N')
$commandJson = @{
    holdId = [string]$hold.id;
    buyerId = $buyerId;
    idempotencyKey = $checkoutKey
} | ConvertTo-Json -Compress
$accepted = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
    -ContentType 'application/json' -Body $commandJson -UseBasicParsing
Require ([int]$accepted.StatusCode -eq 202) "first checkout must return HTTP 202, got $($accepted.StatusCode)"
$submission = $accepted.Content | ConvertFrom-Json
Require ($null -ne $submission.orderId) 'HTTP checkout did not return an order ID'
$orderId = [string]$submission.orderId
$location = [string]$accepted.Headers['Location']
Require ($location -eq "/checkouts/${orderId}?buyerId=$buyerId") 'invalid Location header'

$replay = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
    -ContentType 'application/json' -Body $commandJson -UseBasicParsing
Require ([int]$replay.StatusCode -eq 200) 'repeated submission must return HTTP 200'
$replayed = $replay.Content | ConvertFrom-Json
Require ([string]$replayed.orderId -eq $orderId) 'repeated submission returned a different order'

$state = 'Pending'
$status = $null
for ($i = 0; $i -lt 90; $i++) {
    $status = Invoke-RestMethod -Uri "$OrderingUrl$location"
    $state = [string]$status.state
    if ($state -eq 'Completed' -or $state -eq 'Failed') { break }
    Start-Sleep -Seconds 1
}
Require ($state -eq 'Completed') "checkout did not complete (state=$state; reason=$($status.failureReasonCode))"
Require ([string]$status.holdId -eq [string]$hold.id) 'status has incorrect hold ID'

# Verify that the new HTTP API triggered the existing saga exactly once.
$orderCounts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM order_intents WHERE id = '$orderId'), (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'CheckoutRequestedV1'), (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'InventorySaleRequestedV1'), (SELECT count(*) FROM ordering_result_inbox WHERE order_id = '$orderId' AND message_type = 'InventorySaleCommittedV1');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify Ordering database'
Require ($orderCounts.Trim() -eq '1,1,1,1') "unexpected Ordering records: $orderCounts"
$inventoryCounts = (docker compose exec -T postgres psql -X -U ticketrush `
    -d ticketrush -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_result_outbox WHERE order_id = '$orderId'), (SELECT count(*) FROM orders WHERE id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify Inventory database'
Require ($inventoryCounts.Trim() -eq '1,1,1') "unexpected Inventory records: $inventoryCounts"
Write-Host 'Ordering HTTP checkout saga smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Seat: $($seat.seatCode) | Final state: $state"
Write-Host 'HTTP 202 acceptance; HTTP 200 replay; one fact, one command, one sale and one result.'
