# Phase 2.3l: HTTP -> Ordering -> Payment authorization -> Inventory sale -> Ordering outcome.
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

$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$attackerBuyer = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
$aliceToken = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$bobToken = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username bob
$authAlice = @{ Authorization = "Bearer $aliceToken" }
$authBob = @{ Authorization = "Bearer $bobToken" }
# Without a bearer token, hold admission must be rejected.
$unauthorized = $false
try {
    $null = Invoke-WebRequest -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
        -ContentType 'application/json' -Body '{}' -UseBasicParsing -ErrorAction Stop
}
catch {
    if ($null -ne $_.Exception.Response) {
        $unauthorized = ([int]$_.Exception.Response.StatusCode -eq 401)
    }
}
Require $unauthorized 'anonymous hold creation was not rejected with HTTP 401'

$holdKey = 'api-saga-hold-' + [guid]::NewGuid().ToString('N')
$hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $authAlice -ContentType 'application/json' -Body (@{
        buyerId = $buyerId; idempotencyKey = $holdKey;
        seatCodes = @($seat.seatCode)
    } | ConvertTo-Json -Depth 5)
Require ($null -ne $hold.id) 'could not create Booking hold'
Require (-not [string]::IsNullOrWhiteSpace([string]$hold.holdProof)) 'Booking did not return a hold proof'

# Negative admission test: knowing the hold ID and providing another buyer ID
# must NOT reserve the hold in Ordering, even with the correct signed proof.
$forgedCommand = @{
    holdId = [string]$hold.id;
    buyerId = $attackerBuyer;
    holdProof = [string]$hold.holdProof;
    idempotencyKey = 'unauthorized-' + [guid]::NewGuid().ToString('N')
} | ConvertTo-Json -Compress
$forbidden = $false
try {
    $null = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
        -Headers $authBob -ContentType 'application/json' -Body $forgedCommand -UseBasicParsing -ErrorAction Stop
}
catch {
    if ($null -ne $_.Exception.Response) {
        $forbidden = ([int]$_.Exception.Response.StatusCode -eq 403)
    }
}
Require $forbidden 'another buyer was not rejected with HTTP 403'

$checkoutKey = 'api-saga-checkout-' + [guid]::NewGuid().ToString('N')
$commandJson = @{
    holdId = [string]$hold.id;
    buyerId = $buyerId;
    holdProof = [string]$hold.holdProof;
    idempotencyKey = $checkoutKey
} | ConvertTo-Json -Compress
$accepted = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
    -Headers $authAlice -ContentType 'application/json' -Body $commandJson -UseBasicParsing
Require ([int]$accepted.StatusCode -eq 202) "first checkout must return HTTP 202, got $($accepted.StatusCode)"
$submission = $accepted.Content | ConvertFrom-Json
Require ($null -ne $submission.orderId) 'HTTP checkout did not return an order ID'
$orderId = [string]$submission.orderId
$location = [string]$accepted.Headers['Location']
Require ($location -eq "/checkouts/${orderId}?buyerId=$buyerId") 'invalid Location header'

$replay = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
    -Headers $authAlice -ContentType 'application/json' -Body $commandJson -UseBasicParsing
Require ([int]$replay.StatusCode -eq 200) 'repeated submission must return HTTP 200'
$replayed = $replay.Content | ConvertFrom-Json
Require ([string]$replayed.orderId -eq $orderId) 'repeated submission returned a different order'

$state = 'Pending'
$status = $null
for ($i = 0; $i -lt 90; $i++) {
    $status = Invoke-RestMethod -Uri "$OrderingUrl$location" -Headers $authAlice
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
$paymentStep = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'PaymentAuthorizationRequestedV1'), (SELECT count(*) FROM ordering_payment_inbox WHERE order_id = '$orderId' AND message_type = 'PaymentAuthorizedV1');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify Ordering Payment inbox'
Require ($paymentStep.Trim() -eq '1,1') "incorrect Payment saga records: $paymentStep"
$paymentPersisted = (docker compose exec -T payment-postgres psql -X -U ticketrush `
    -d ticketrush_payment -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM payment_receipts WHERE order_id = '$orderId' AND result_type = 'PaymentAuthorizedV1'), (SELECT count(*) FROM payment_outbox WHERE order_id = '$orderId' AND published_at_utc IS NOT NULL);") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify Payment database'
Require ($paymentPersisted.Trim() -eq '1,1') "incorrect durable Payment records: $paymentPersisted"
$inventoryCounts = (docker compose exec -T postgres psql -X -U ticketrush `
    -d ticketrush -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_result_outbox WHERE order_id = '$orderId'), (SELECT count(*) FROM orders WHERE id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify Inventory database'
Require ($inventoryCounts.Trim() -eq '1,1,1') "unexpected Inventory records: $inventoryCounts"
Write-Host 'Ordering HTTP checkout saga smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Seat: $($seat.seatCode) | Final state: $state"
Write-Host 'HTTP 202/200; one Payment authorization, one Inventory sale, one terminal Ordering result.'
