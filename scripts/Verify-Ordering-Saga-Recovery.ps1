# Disposable local smoke: simulate an accepted checkout fact whose original
# publication was recorded, but whose consumer delivery was lost.
# The watchdog requeues the SAME fact; the saga then rejects a nonexistent hold.
# No real seat is sold. Requires running Docker Compose services.
param([string]$OrderingUrl = 'http://localhost:8091')
$ErrorActionPreference = 'Stop'
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Ordering saga recovery smoke FAILED: $message" }
}
$orderId = [guid]::NewGuid().ToString('D')
$holdId = [guid]::NewGuid().ToString('D')
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$auth = @{ Authorization = "Bearer $token" }
$messageId = [guid]::NewGuid().ToString('D')

# Deliberately seed a 5-minute-old checkout with a broker-confirmed flag
# but without delivery to its consumer. This bypasses HTTP admission only to
# simulate a missing broker event; it is not a successful customer checkout.
$sql = @"
BEGIN;
INSERT INTO order_intents
    (id,hold_id,buyer_id,idempotency_key,state,requested_at_utc)
VALUES ('$orderId'::uuid, '$holdId'::uuid, '$buyerId'::uuid,
        'recovery-smoke-$messageId', 0, clock_timestamp() - interval '5 minutes');
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,published_at_utc,
     publish_attempts,next_attempt_at_utc,last_error)
SELECT '$messageId'::uuid, id, 'CheckoutRequestedV1',
    jsonb_build_object('MessageId','$messageId','OrderId',id,
        'HoldId',hold_id,'BuyerId',buyer_id,'RequestedAtUtc',requested_at_utc),
    requested_at_utc, requested_at_utc + interval '1 second', 1, NULL, NULL
FROM order_intents WHERE id = '$orderId'::uuid;
COMMIT;
"@
$sql | docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not insert synthetic overdue checkout'

$observed = $null
for ($i = 0; $i -lt 90; $i++) {
    $observed = Invoke-RestMethod -Uri "$OrderingUrl/checkouts/$orderId`?buyerId=$buyerId" -Headers $auth
    if ($observed.state -eq 'Failed' -and $observed.recoveryAttempts -ge 1) { break }
    Start-Sleep -Seconds 1
}
Require ($null -ne $observed) 'checkout status could not be loaded'
Require ($observed.state -eq 'Failed') "expected rejected nonexistent hold, got $($observed.state)"
Require ($observed.recoveryAttempts -ge 1) 'watchdog did not record an overdue recovery'
Require (-not $observed.recoveryNeedsAttention) 'terminal checkout must clear attention status'

$counts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'InventorySaleRequestedV1'), (SELECT count(*) FROM ordering_result_inbox WHERE order_id = '$orderId' AND message_type = 'InventorySaleRejectedV1');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not inspect Ordering recovery database state'
Require ($counts.Trim() -eq '1,1') "expected one Inventory command and one rejection: $counts"

Write-Host 'Ordering saga recovery smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Original checkout fact: $messageId | recovery checks: $($observed.recoveryAttempts)"
Write-Host 'Missing event recovered with stable MessageId; Inventory rejected missing hold; no seat sold.'
