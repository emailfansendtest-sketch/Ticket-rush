# Exercises Ordering outbox -> RabbitMQ -> Notification inbox in the live Docker stack.
# Synthetic terminal record: no Inventory sale, email, SMS, or Payment is performed.
param([int]$TimeoutSeconds = 90)
$ErrorActionPreference = 'Stop'

function Query-Ordering([string]$sql) {
    $lines = & docker compose exec -T ordering-postgres psql -X -U ticketrush `
        -d ticketrush_ordering -A -t -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw 'Notification smoke FAILED: Ordering SQL failed.' }
    return ($lines | Out-String).Trim()
}
function Query-Notification([string]$sql) {
    $lines = & docker compose exec -T notification-postgres psql -X -U ticketrush `
        -d ticketrush_notification -A -t -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw 'Notification smoke FAILED: Notification SQL failed.' }
    return ($lines | Out-String).Trim()
}
function Wait-For([scriptblock]$condition, [string]$label) {
    for ($i = 0; $i -lt $TimeoutSeconds; $i++) {
        if (& $condition) { return }
        Start-Sleep -Seconds 1
    }
    throw "Notification smoke FAILED: timed out waiting for $label. Check docker compose logs notification-worker ordering-outbox."
}

$orderId = [Guid]::NewGuid().ToString('D')
$messageId = [Guid]::NewGuid().ToString('D')
$holdId = [Guid]::NewGuid().ToString('D')
$buyerId = [Guid]::NewGuid().ToString('D')

$sql = @"
BEGIN;
INSERT INTO order_intents
    (id,hold_id,buyer_id,idempotency_key,state,requested_at_utc,
     payment_required,simulate_payment_decline)
VALUES ('$orderId'::uuid, '$holdId'::uuid, '$buyerId'::uuid,
        'notification-smoke-$messageId', 1, clock_timestamp(), FALSE, FALSE);
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,
     published_at_utc,publish_attempts,next_attempt_at_utc,last_error)
SELECT '$messageId'::uuid, id, 'CheckoutNotificationRequestedV1',
    jsonb_build_object('MessageId','$messageId','OrderId',id,
      'BuyerId',buyer_id,'Outcome','Completed','ReasonCode',NULL,
      'OccurredAtUtc',requested_at_utc),
    requested_at_utc, NULL, 0, NULL, NULL
FROM order_intents WHERE id = '$orderId'::uuid;
COMMIT;
"@
$sql | docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Notification smoke FAILED: could not seed synthetic outbox.' }

Wait-For {
    $state = Query-Ordering "SELECT COUNT(*) FROM outbox_messages WHERE message_id = '$messageId' AND published_at_utc IS NOT NULL;"
    $receipt = Query-Notification "SELECT COUNT(*) FROM notification_receipts WHERE message_id = '$messageId' AND order_id = '$orderId' AND outcome = 'Completed';"
    return ($state -eq '1' -and $receipt -eq '1')
} 'first confirmed notification publication and receipt'

# Re-arm the SAME outbox row. At-least-once publication must not create a second receipt.
$before = Query-Ordering "SELECT publish_attempts FROM outbox_messages WHERE message_id = '$messageId';"
$beforeNumber = [int]$before
$updated = Query-Ordering "WITH changed AS (UPDATE outbox_messages SET published_at_utc = NULL, next_attempt_at_utc = NULL WHERE message_id = '$messageId' AND published_at_utc IS NOT NULL RETURNING 1) SELECT COUNT(*) FROM changed;"
if ($updated -ne '1') { throw 'Notification smoke FAILED: could not re-arm original outbox message.' }

Wait-For {
    $state = Query-Ordering "SELECT COUNT(*) FROM outbox_messages WHERE message_id = '$messageId' AND published_at_utc IS NOT NULL AND publish_attempts > $beforeNumber;"
    return ($state -eq '1')
} 'second confirmed publication of same message'

# Ensure the replay has also been acknowledged by the live RabbitMQ consumer.
# A database count alone would not detect a continuously requeued duplicate.
$basicAuth = 'Basic ' + [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
Wait-For {
    $queue = Invoke-RestMethod -Method Get `
        -Uri 'http://localhost:15672/api/queues/%2F/ticket.notification.checkout-finalized.v1' `
        -Headers @{ Authorization = $basicAuth }
    return ([int]$queue.messages_ready -eq 0 -and
            [int]$queue.messages_unacknowledged -eq 0)
} 'notification command acknowledgment after replay'

# ACK/receipt idempotency: after redelivery settles, there must still be one stored receipt.
$receiptCount = Query-Notification "SELECT COUNT(*) FROM notification_receipts WHERE order_id = '$orderId';"
if ($receiptCount -ne '1') { throw "Notification smoke FAILED: $receiptCount receipts instead of one." }

Write-Host 'Notification service RabbitMQ smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Event: $messageId | Outcome: Completed | receipts: 1"
Write-Host 'Outbox republished the same message; Notification stored one simulated delivery. No seat sold or external message sent.'
