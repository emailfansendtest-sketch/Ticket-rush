# Synthetic end-to-end test of Payment authorization -> Inventory rejection -> void.
# No real hold exists; no seat is sold, no money is captured or refunded.
param([string]$OrderingUrl = 'http://localhost:8091')
$ErrorActionPreference = 'Stop'
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Payment void smoke FAILED: $message" }
}
$orderId = [guid]::NewGuid().ToString('D')
$holdId = [guid]::NewGuid().ToString('D')
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$messageId = [guid]::NewGuid().ToString('D')
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$auth = @{ Authorization = "Bearer $token" }

# Only the synthetic test setup bypasses HTTP hold-proof admission. The actual
# Payment, Inventory, compensation, outbox and Ordering workers are exercised.
$sql = @"
BEGIN;
INSERT INTO order_intents
    (id,hold_id,buyer_id,idempotency_key,state,requested_at_utc,
     payment_required,simulate_payment_decline)
VALUES ('$orderId'::uuid, '$holdId'::uuid, '$buyerId'::uuid,
        'payment-void-smoke-$messageId', 0, clock_timestamp(), TRUE, FALSE);
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,
     published_at_utc,publish_attempts,next_attempt_at_utc,last_error)
SELECT '$messageId'::uuid, id, 'CheckoutRequestedV1',
    jsonb_build_object('MessageId','$messageId','OrderId',id,
        'HoldId',hold_id,'BuyerId',buyer_id,'RequestedAtUtc',requested_at_utc),
    requested_at_utc, NULL, 0, NULL, NULL
FROM order_intents WHERE id = '$orderId'::uuid;
COMMIT;
"@
$sql | docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not seed checkout intent'

$status = $null
for ($i = 0; $i -lt 120; $i++) {
    $status = Invoke-RestMethod -Uri "$OrderingUrl/checkouts/${orderId}?buyerId=$buyerId" -Headers $auth
    if ([string]$status.state -eq 'Failed' -and [string]$status.compensationStatus -eq 'Voided') { break }
    Start-Sleep -Seconds 1
}
Require ($null -ne $status) 'checkout status missing'
Require ([string]$status.state -eq 'Failed') "expected Inventory rejection, got $($status.state)"
Require ([string]$status.compensationStatus -eq 'Voided') "expected confirmed void, got $($status.compensationStatus)"

$orderingCounts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM ordering_payment_inbox WHERE order_id = '$orderId' AND message_type = 'PaymentAuthorizedV1'), (SELECT count(*) FROM ordering_result_inbox WHERE order_id = '$orderId' AND message_type = 'InventorySaleRejectedV1'), (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'PaymentVoidRequestedV1'), (SELECT count(*) FROM ordering_void_inbox WHERE order_id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not query Ordering'
Require ($orderingCounts.Trim() -eq '1,1,1,1') "unexpected Ordering rows: $orderingCounts"

$paymentCounts = (docker compose exec -T payment-postgres psql -X -U ticketrush `
    -d ticketrush_payment -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM payment_receipts WHERE order_id = '$orderId' AND voided_at_utc IS NOT NULL), (SELECT count(*) FROM payment_void_receipts WHERE order_id = '$orderId'), (SELECT count(*) FROM payment_outbox WHERE order_id = '$orderId' AND message_type = 'PaymentVoidedV1' AND published_at_utc IS NOT NULL);") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not query Payment'
Require ($paymentCounts.Trim() -eq '1,1,1') "unexpected Payment compensation rows: $paymentCounts"

$inventoryCounts = (docker compose exec -T postgres psql -X -U ticketrush `
    -d ticketrush -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM orders WHERE id = '$orderId'), (SELECT count(*) FROM seats WHERE sold_order_id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not query Inventory'
Require ($inventoryCounts.Trim() -eq '0,0') "compensation smoke must not sell seats: $inventoryCounts"

Write-Host 'Payment authorization void compensation smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Checkout: Failed | Compensation: Voided"
Write-Host 'Simulated authorization was voided after Inventory rejected the missing hold; no seat sold or funds captured.'
