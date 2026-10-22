# Local Docker Compose smoke: one real inventory sale command delivered over RabbitMQ.
# This consumes one available seat in the seeded demo event. Do not run against production.
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$ManagementUrl = 'http://localhost:15672'
)
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
$ManagementUrl = $ManagementUrl.TrimEnd('/')
$eventId = '11111111-1111-1111-1111-111111111111'

function Require($condition, $message) { if (-not $condition) { throw "Inventory consumer smoke FAILED: $message" } }
function Post-Command($message, $auth) {
    $body = @{
        properties = @{
            delivery_mode = 2
            content_type = 'application/json'
            type = 'InventorySaleRequestedV1'
            message_id = $message.MessageId
        }
        routing_key = 'inventory.sale-requested.v1'
        payload = ($message | ConvertTo-Json -Compress -Depth 6)
        payload_encoding = 'string'
    } | ConvertTo-Json -Compress -Depth 8
    $reply = Invoke-RestMethod -Method Post -Uri "$ManagementUrl/api/exchanges/%2F/ticket.events/publish" `
        -ContentType 'application/json' -Headers @{ Authorization = $auth } -Body $body
    Require ($reply.routed -eq $true) 'command was not routed to Inventory queue'
}

# Windows PowerShell can keep a JSON array nested when Invoke-RestMethod is wrapped in @(...).
$seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
$seat = $seats | Where-Object {
    $_.status -eq 0 -or $_.status -eq 'Available'
} | Select-Object -First 1
Require ($null -ne $seat) 'no available demo seats remain'
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$authBuyer = @{ Authorization = "Bearer $token" }
$holdKey = 'inventory-consumer-smoke-' + [guid]::NewGuid().ToString('N')
$hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $authBuyer -ContentType 'application/json' -Body (@{
        buyerId = $buyerId; idempotencyKey = $holdKey; seatCodes = @($seat.seatCode)
    } | ConvertTo-Json -Depth 5)
$orderId = [guid]::NewGuid().ToString('D')
$messageId = [guid]::NewGuid().ToString('D')
$command = @{
    MessageId = $messageId; OrderId = $orderId; HoldId = $hold.id
    BuyerId = $buyerId; RequestedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
Post-Command $command $auth

# Wait for the inbox + result-outbox DB transaction to commit.
$sql = "SELECT message_type FROM inventory_result_outbox WHERE order_id = '$orderId';"
$result = ''
for ($i=0; $i -lt 30; $i++) {
    $result = (docker compose exec -T postgres psql -U ticketrush -d ticketrush -At -c $sql) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL inspection failed' }
    if ($result -match 'InventorySaleCommittedV1') { break }
    Start-Sleep -Seconds 1
}
Require ($result -match 'InventorySaleCommittedV1') "sale result not committed: $result"

# At-least-once delivery: repeat the SAME message; it must not issue another result.
Post-Command $command $auth
Start-Sleep -Seconds 3
$counts = (docker compose exec -T postgres psql -U ticketrush -d ticketrush -At -F ',' -c `
    "SELECT (SELECT count(*) FROM inventory_inbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_result_outbox WHERE order_id = '$orderId'), (SELECT count(*) FROM inventory_sales WHERE order_id = '$orderId');") -join "`n"
Require ($LASTEXITCODE -eq 0) 'database count query failed'
Require ($counts.Trim() -eq '1,1,1') "expected one inbox, outbox, sale; got '$counts'"
Write-Host 'Inventory consumer RabbitMQ smoke: PASS' -ForegroundColor Green
Write-Host "Order: $orderId | Command: $messageId | seat: $($seat.seatCode)"
Write-Host 'One sale, one inbox receipt and one durable result outbox message; no result event published yet.'
