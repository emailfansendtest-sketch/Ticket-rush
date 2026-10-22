# Phase 2.3l: payment rejection must NOT cause an Inventory sale.
# Creates one hold in disposable demo data. No seat is sold by this test.
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091'
)
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Payment decline checkout smoke FAILED: $message" }
}
$eventId = '11111111-1111-1111-1111-111111111111'
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$auth = @{ Authorization = "Bearer $token" }
$seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
$seat = $seats | Where-Object {
    $_.status -eq 0 -or $_.status -eq 'Available'
} | Select-Object -First 1
Require ($null -ne $seat) 'no available demo seat remains'
$hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $auth -ContentType 'application/json' -Body (@{
        buyerId = $buyerId; idempotencyKey = 'payment-decline-hold-' + [guid]::NewGuid().ToString('N');
        seatCodes = @($seat.seatCode)
    } | ConvertTo-Json -Depth 5)
Require (-not [string]::IsNullOrWhiteSpace([string]$hold.holdProof)) 'missing hold proof'
$checkout = @{
    holdId = [string]$hold.id;
    buyerId = $buyerId;
    holdProof = [string]$hold.holdProof;
    idempotencyKey = 'payment-decline-checkout-' + [guid]::NewGuid().ToString('N');
    simulatePaymentDecline = $true
} | ConvertTo-Json -Compress
$accepted = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$OrderingUrl/checkouts" `
    -Headers $auth -ContentType 'application/json' -Body $checkout
Require ([int]$accepted.StatusCode -eq 202) 'checkout was not durably admitted'
$orderId = [string](($accepted.Content | ConvertFrom-Json).orderId)
Require (-not [string]::IsNullOrWhiteSpace($orderId)) 'missing orderId'
$status = $null
for ($i = 0; $i -lt 90; $i++) {
    $status = Invoke-RestMethod -Uri "$OrderingUrl/checkouts/${orderId}?buyerId=$buyerId" -Headers $auth
    if ([string]$status.state -ne 'Pending') { break }
    Start-Sleep -Seconds 1
}
Require ([string]$status.state -eq 'Failed') "expected Failed, found $($status.state)"
Require ([string]$status.failureReasonCode -eq 'SimulatedDecline') "wrong reason: $($status.failureReasonCode)"
$orderingCounts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM ordering_payment_inbox WHERE order_id = '$orderId' AND message_type = 'PaymentDeclinedV1'), (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'InventorySaleRequestedV1');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not query Ordering'
Require ($orderingCounts.Trim() -eq '1,0') "Payment decline incorrectly scheduled inventory: $orderingCounts"
$inventoryCounts = (docker compose exec -T postgres psql -X -U ticketrush `
    -d ticketrush -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'), (SELECT count(*) FROM orders WHERE id = '$orderId'), (SELECT count(*) FROM seats WHERE sold_order_id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not query Inventory'
Require ($inventoryCounts.Trim() -eq '0,0,0') "Inventory was modified by declined payment: $inventoryCounts"
Write-Host 'Payment decline checkout saga smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Seat: $($seat.seatCode) | Payment declined, no sale."
