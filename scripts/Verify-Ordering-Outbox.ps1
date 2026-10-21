$ErrorActionPreference = 'Stop'

# This creates a synthetic order intent in the local Ordering database.
# It does NOT call Inventory and does NOT sell a seat.
$orderId = [guid]::NewGuid().ToString('D')
$messageId = [guid]::NewGuid().ToString('D')
$holdId = [guid]::NewGuid().ToString('D')
$buyerId = [guid]::NewGuid().ToString('D')
$timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.ffffffZ', [System.Globalization.CultureInfo]::InvariantCulture)
$key = "outbox-smoke-$orderId"
$json = @{
    messageId = $messageId
    orderId = $orderId
    holdId = $holdId
    buyerId = $buyerId
    requestedAtUtc = $timestamp
} | ConvertTo-Json -Compress

$sql = @"
BEGIN;
INSERT INTO order_intents
    (id, hold_id, buyer_id, idempotency_key, state, requested_at_utc)
VALUES ('$orderId', '$holdId', '$buyerId', '$key', 0, '$timestamp');
INSERT INTO outbox_messages
    (message_id, order_id, message_type, payload_json,
     created_at_utc, published_at_utc, publish_attempts,
     next_attempt_at_utc, last_error)
VALUES ('$messageId', '$orderId', 'CheckoutRequestedV1', '$json'::jsonb,
        '$timestamp', NULL, 0, NULL, NULL);
COMMIT;
"@

$sql | & docker compose exec -T ordering-postgres psql -U ticketrush -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not seed the synthetic Ordering outbox message.' }

$published = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $query = "SELECT COALESCE(published_at_utc::text,'') FROM outbox_messages WHERE message_id='$messageId';"
    $value = $query | & docker compose exec -T ordering-postgres psql -U ticketrush -d ticketrush_ordering -v ON_ERROR_STOP=1 -At
    if ($LASTEXITCODE -ne 0) { throw 'Could not query the Ordering outbox.' }
    if (-not [string]::IsNullOrWhiteSpace(($value | Out-String))) {
        $published = $true
        break
    }
}

if (-not $published) {
    throw "Outbox message $messageId has not been marked delivered; inspect 'docker compose logs ordering-outbox rabbitmq'."
}

Write-Host 'Ordering outbox RabbitMQ smoke: PASS'
Write-Host "Order: $orderId | Message: $messageId"
Write-Host 'The message was broker-confirmed and marked published in Ordering PostgreSQL.'
Write-Host 'This is a synthetic record; no Inventory sale was performed.'
