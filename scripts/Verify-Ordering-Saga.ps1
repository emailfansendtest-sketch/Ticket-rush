# End-to-end local development smoke: Ordering fact -> command -> Inventory sale
# -> Inventory result -> Ordering Completed. Consumes one disposable demo seat.
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$ManagementUrl = 'http://localhost:15672'
)
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
$ManagementUrl = $ManagementUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Ordering saga smoke FAILED: $message" }
}
$eventId = '11111111-1111-1111-1111-111111111111'
$seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
$seat = $seats | Where-Object {
    $_.status -eq 0 -or $_.status -eq 'Available'
} | Select-Object -First 1
Require ($null -ne $seat) 'no available demo seats remain'
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$authBuyer = @{ Authorization = "Bearer $token" }
$holdKey = 'saga-smoke-' + [guid]::NewGuid().ToString('N')
$hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $authBuyer -ContentType 'application/json' -Body (@{
        buyerId = $buyerId; idempotencyKey = $holdKey;
        seatCodes = @($seat.seatCode)
    } | ConvertTo-Json -Depth 5)
Require ($null -ne $hold.id) 'hold API did not return an ID'
$orderId = [guid]::NewGuid().ToString('D')
$messageId = [guid]::NewGuid().ToString('D')
$holdId = [string]$hold.id
# The public /orders route is still the original synchronous implementation.
# Seed the independent Ordering intake database, including its durable fact.
# The timestamp comes from a single PostgreSQL value to avoid precision drift.
$sql = @"
BEGIN;
INSERT INTO order_intents (id,hold_id,buyer_id,idempotency_key,state,requested_at_utc)
VALUES ('$orderId'::uuid,'$holdId'::uuid,'$buyerId'::uuid,
        'saga-smoke-$messageId',0,clock_timestamp());
INSERT INTO outbox_messages
    (message_id,order_id,message_type,payload_json,created_at_utc,published_at_utc,
     publish_attempts,next_attempt_at_utc,last_error)
SELECT '$messageId'::uuid, id, 'CheckoutRequestedV1',
    jsonb_build_object('MessageId','$messageId','OrderId',id,
        'HoldId',hold_id,'BuyerId',buyer_id,'RequestedAtUtc',requested_at_utc),
    requested_at_utc, NULL, 0, NULL, NULL
FROM order_intents WHERE id = '$orderId'::uuid;
COMMIT;
"@
$sql | docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -v ON_ERROR_STOP=1 | Out-Null
Require ($LASTEXITCODE -eq 0) 'could not seed Ordering intent/outbox'

$state = ''
for ($i=0; $i -lt 75; $i++) {
    $state = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
        -d ticketrush_ordering -At -v ON_ERROR_STOP=1 -c `
        "SELECT state FROM order_intents WHERE id = '$orderId';") -join "`n"
    Require ($LASTEXITCODE -eq 0) 'could not inspect Ordering state'
    if ($state.Trim() -eq '1' -or $state.Trim() -eq '2') { break }
    Start-Sleep -Seconds 1
}
Require ($state.Trim() -eq '1') "Ordering did not reach Completed (state=$state)"

# Both databases must agree; Ordering's inbox must have the result message.
$orderingCounts = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'InventorySaleRequestedV1'), (SELECT count(*) FROM ordering_result_inbox WHERE order_id = '$orderId' AND message_type = 'InventorySaleCommittedV1');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not count Ordering saga records'
Require ($orderingCounts.Trim() -eq '1,1') "Ordering expected one command and one result: $orderingCounts"
$inventoryCounts = (docker compose exec -T inventory-postgres psql -X -U ticketrush `
    -d ticketrush_inventory -At -F ',' -v ON_ERROR_STOP=1 -c `
    "SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_result_outbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_sales WHERE order_id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not count Inventory transaction records'
Require ($inventoryCounts.Trim() -eq '1,1,1') "Inventory expected one inbox, result and sale: $inventoryCounts"

# Republish the original fact to exercise idempotent redelivery at the start
# of the workflow (same MessageId and exact stored JSON payload).
$originalJson = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -v ON_ERROR_STOP=1 -c `
    "SELECT payload_json::text FROM outbox_messages WHERE message_id = '$messageId';") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not load original checkout message'
$auth = 'Basic ' + [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
$body = @{
    properties = @{ delivery_mode = 2; content_type = 'application/json';
        type = 'CheckoutRequestedV1'; message_id = $messageId }
    routing_key = 'checkout.requested.v1'
    payload = $originalJson.Trim()
    payload_encoding = 'string'
} | ConvertTo-Json -Compress -Depth 8
$published = Invoke-RestMethod -Method Post `
    -Uri "$ManagementUrl/api/exchanges/%2F/ticket.events/publish" `
    -Headers @{ Authorization = $auth } -ContentType 'application/json' -Body $body
Require ($published.routed -eq $true) 'duplicate event was not routed'
Start-Sleep -Seconds 3
$commandCount = (docker compose exec -T ordering-postgres psql -X -U ticketrush `
    -d ticketrush_ordering -At -v ON_ERROR_STOP=1 -c `
    "SELECT count(*) FROM outbox_messages WHERE order_id = '$orderId' AND message_type = 'InventorySaleRequestedV1';") -join "`n"
Require ($LASTEXITCODE -eq 0) 'could not verify command deduplication'
Require ($commandCount.Trim() -eq '1') "duplicate checkout created extra commands: $commandCount"
Write-Host 'Ordering saga RabbitMQ smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Checkout fact: $messageId | seat: $($seat.seatCode)"
Write-Host 'Completed state, exactly one command, Inventory sale and Ordering result inbox.'
