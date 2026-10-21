# End-to-end recovery of a lost Inventory result (no seat sale).
# Requires disposable local Docker Compose data and running Ordering/Inventory workers.
# Simulates an Inventory command processed long ago whose ORIGINAL result was
# marked broker-confirmed but never reached Ordering. The Ordering watchdog
# replays that command; Inventory's inbox restores the same result outbox row.
param([string]$OrderingUrl = 'http://localhost:8091')
$ErrorActionPreference = 'Stop'
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Inventory result recovery smoke FAILED: $message" }
}

$orderId = [guid]::NewGuid().ToString('D')
$holdId = [guid]::NewGuid().ToString('D')
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$auth = @{ Authorization = "Bearer $token" }
$checkoutFactId = [guid]::NewGuid().ToString('D')
$commandId = [guid]::NewGuid().ToString('D')
$resultId = [guid]::NewGuid().ToString('D')

# Seed Inventory FIRST: it has a previously handled command and an already
# confirmed result. The nonexistent hold means no seat was sold. No RabbitMQ
# message is sent by this setup; publication is triggered solely by recovery.
$inventorySql = @"
BEGIN;
INSERT INTO inventory_inbox
    (message_id,order_id,hold_id,buyer_id,processed_at_utc,result_message_id,result_type)
VALUES
    ('$commandId'::uuid,'$orderId'::uuid,'$holdId'::uuid,'$buyerId'::uuid,
     clock_timestamp() - interval '4 minutes','$resultId'::uuid,'InventorySaleRejectedV1');
INSERT INTO inventory_result_outbox
    (message_id,order_id,message_type,payload_json,created_at_utc,published_at_utc,
     publish_attempts,next_attempt_at_utc,last_error)
VALUES
    ('$resultId'::uuid,'$orderId'::uuid,'InventorySaleRejectedV1',
     jsonb_build_object('MessageId','$resultId','OrderId','$orderId','HoldId','$holdId',
                       'ReasonCode','HoldNotFound','RejectedAtUtc',clock_timestamp() - interval '4 minutes'),
     clock_timestamp() - interval '4 minutes',clock_timestamp() - interval '3 minutes',
     1,NULL,NULL);
COMMIT;
"@
$inventorySql | docker compose exec -T postgres psql -X -U ticketrush -d ticketrush -v ON_ERROR_STOP=1 | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not seed original Inventory inbox/result'

# Seed Ordering SECOND: a Pending intent, its original checkout fact, and an
# Inventory command all appear previously published. The existing watchdog
# must re-arm the SAME command, never create a second one.
$orderingSql = @"
BEGIN;
INSERT INTO order_intents
    (id,hold_id,buyer_id,idempotency_key,state,requested_at_utc)
VALUES
    ('$orderId'::uuid,'$holdId'::uuid,'$buyerId'::uuid,'result-recovery-$orderId',
     0,clock_timestamp() - interval '5 minutes');
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,published_at_utc,
     publish_attempts,next_attempt_at_utc,last_error)
SELECT '$checkoutFactId'::uuid,id,'CheckoutRequestedV1',
       jsonb_build_object('MessageId','$checkoutFactId','OrderId',id,
           'HoldId',hold_id,'BuyerId',buyer_id,'RequestedAtUtc',requested_at_utc),
       requested_at_utc,requested_at_utc + interval '1 second',1,NULL,NULL
FROM order_intents WHERE id = '$orderId'::uuid;
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,published_at_utc,
     publish_attempts,next_attempt_at_utc,last_error)
SELECT '$commandId'::uuid,id,'InventorySaleRequestedV1',
       jsonb_build_object('MessageId','$commandId','OrderId',id,
           'HoldId',hold_id,'BuyerId',buyer_id,'RequestedAtUtc',requested_at_utc),
       requested_at_utc + interval '2 seconds',requested_at_utc + interval '3 seconds',
       1,NULL,NULL
FROM order_intents WHERE id = '$orderId'::uuid;
COMMIT;
"@
$orderingSql | docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not seed overdue Ordering checkout and published command'

$observed = $null
for ($i = 0; $i -lt 90; $i++) {
    $observed = Invoke-RestMethod -Uri "$OrderingUrl/checkouts/$orderId`?buyerId=$buyerId" -Headers $auth
    if ($observed.state -eq 'Failed' -and $observed.recoveryAttempts -ge 1) { break }
    Start-Sleep -Seconds 1
}
Require ($null -ne $observed) 'status endpoint did not return a response'
Require ($observed.state -eq 'Failed') "missing result was not restored; state=$($observed.state)"
Require ($observed.recoveryAttempts -ge 1) 'watchdog did not replay Inventory command'
Require (-not $observed.recoveryNeedsAttention) 'resolved order still requires operator attention'
Require ($observed.failureReasonCode -eq 'HoldNotFound') 'unexpected rejection reason'

# All three tiers must agree, including stable identifiers and no sale.
$inventoryVerifySql = @"
SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'),
       (SELECT count(*) FROM inventory_result_outbox WHERE order_id = '$orderId'
            AND message_id = '$resultId' AND published_at_utc IS NOT NULL
            AND publish_attempts >= 2),
       (SELECT count(*) FROM orders WHERE id = '$orderId');
"@
$inventoryCounts = (docker compose exec -T postgres psql -X -U ticketrush -d ticketrush `
    -At -F ',' -v ON_ERROR_STOP=1 -c $inventoryVerifySql) -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not inspect Inventory durable state'
Require ($inventoryCounts.Trim() -eq '1,1,0') "wrong Inventory receipt/result/sale counts: $inventoryCounts"

$orderingVerifySql = @"
SELECT (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId'
            AND message_id = '$commandId' AND message_type = 'InventorySaleRequestedV1'
            AND publish_attempts >= 2),
       (SELECT count(*) FROM ordering_result_inbox WHERE order_id = '$orderId'
            AND message_id = '$resultId' AND message_type = 'InventorySaleRejectedV1');
"@
$orderingCounts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c $orderingVerifySql) -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not inspect Ordering result inbox'
Require ($orderingCounts.Trim() -eq '1,1') "wrong Ordering command/result counts: $orderingCounts"

Write-Host 'Inventory result recovery RabbitMQ smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Original command: $commandId | Original result: $resultId | recovery checks: $($observed.recoveryAttempts)"
Write-Host 'Previously published result was restored with stable MessageId; Inventory did not sell a seat.'
