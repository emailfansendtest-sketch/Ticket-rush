# Local Docker Compose smoke: synthetic rejected Inventory result, real RabbitMQ publish.
# No seat or hold is modified. Requires rabbitmq management API and Docker Compose.
param(
    [string]$ManagementUrl = 'http://localhost:15672'
)
$ErrorActionPreference = 'Stop'
$ManagementUrl = $ManagementUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Inventory result publisher smoke FAILED: $message" }
}

$auth = 'Basic ' + [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
$headers = @{ Authorization = $auth }
$messageId = [guid]::NewGuid().ToString('D')
$orderId = [guid]::NewGuid().ToString('D')
$holdId = [guid]::NewGuid().ToString('D')
$probe = 'ticket.smoke.inventory-result.' + [guid]::NewGuid().ToString('N')
$queueUri = "$ManagementUrl/api/queues/%2F/$probe"
$bindingUri = "$ManagementUrl/api/bindings/%2F/e/ticket.events/q/$probe"
$probeCreated = $false

try {
    # A separate probe queue observes the exact message without touching the
    # permanent durable queue used by the future Ordering saga. The publisher
    # declares the permanent queue lazily on its first publication.
    $queueConfig = @{ durable = $true; auto_delete = $false; arguments = @{} } |
        ConvertTo-Json -Compress -Depth 4
    Invoke-RestMethod -Method Put -Uri $queueUri -Headers $headers `
        -ContentType 'application/json' -Body $queueConfig | Out-Null
    $probeCreated = $true
    $bindingConfig = @{ routing_key = 'inventory.sale-rejected.v1'; arguments = @{} } |
        ConvertTo-Json -Compress -Depth 4
    Invoke-RestMethod -Method Post -Uri $bindingUri -Headers $headers `
        -ContentType 'application/json' -Body $bindingConfig | Out-Null

    # Deliberately insert only a synthetic rejected event. It does not create a
    # sale or claim that an unexecuted sale was committed. Identifiers are GUIDs
    # generated in this script, never untrusted input.
    $sql = @"
BEGIN;
INSERT INTO inventory_result_outbox (
    message_id, order_id, message_type, payload_json, created_at_utc, published_at_utc,
    publish_attempts, next_attempt_at_utc, last_error
) VALUES (
    '$messageId'::uuid, '$orderId'::uuid, 'InventorySaleRejectedV1',
    jsonb_build_object(
        'MessageId', '$messageId', 'OrderId', '$orderId', 'HoldId', '$holdId',
        'ReasonCode', 'SyntheticSmokeTest', 'RejectedAtUtc', clock_timestamp()),
    clock_timestamp(), NULL, 0, NULL, NULL
);
COMMIT;
"@
    $sql | docker compose exec -T postgres psql -X -U ticketrush -d ticketrush -v ON_ERROR_STOP=1 | Out-Null
    Require ($LASTEXITCODE -eq 0) 'could not insert synthetic result into PostgreSQL'

    # Observe the actual broker message on an otherwise empty temporary queue.
    # The API consumes and ACKs only this probe copy; the permanent durable queue
    # retains its own independent copy for the future Ordering result consumer.
    $getRequest = @{ count = 5; ackmode = 'ack_requeue_false'; encoding = 'auto'; truncate = 50000 } |
        ConvertTo-Json -Compress
    $found = $false
    for ($i = 0; $i -lt 35; $i++) {
        $messages = Invoke-RestMethod -Method Post -Uri "$queueUri/get" -Headers $headers `
            -ContentType 'application/json' -Body $getRequest
        foreach ($item in $messages) {
            if ($null -eq $item -or $null -eq $item.properties) { continue }
            if ($item.properties.message_id -ne $messageId) { continue }
            Require ($item.properties.type -eq 'InventorySaleRejectedV1') 'wrong message type'
            Require ($item.routing_key -eq 'inventory.sale-rejected.v1') 'wrong routing key'
            $payload = $item.payload | ConvertFrom-Json
            Require ($payload.MessageId -eq $messageId) 'payload MessageId mismatch'
            Require ($payload.OrderId -eq $orderId) 'payload OrderId mismatch'
            Require ($payload.ReasonCode -eq 'SyntheticSmokeTest') 'payload reason mismatch'
            $found = $true
            break
        }
        if ($found) { break }
        Start-Sleep -Seconds 1
    }
    Require $found 'no matching broker message was observed on the probe queue'

    $sqlStatus = "SELECT coalesce(published_at_utc::text, '') FROM inventory_result_outbox WHERE message_id = '$messageId';"
    $published = ''
    for ($i = 0; $i -lt 15; $i++) {
        $published = (docker compose exec -T postgres psql -X -U ticketrush -d ticketrush -At -v ON_ERROR_STOP=1 -c $sqlStatus) -join "`n"
        Require ($LASTEXITCODE -eq 0) 'could not verify published status'
        if (-not [string]::IsNullOrWhiteSpace($published)) { break }
        Start-Sleep -Seconds 1
    }
    Require (-not [string]::IsNullOrWhiteSpace($published)) 'broker message seen but outbox not marked published'
    $permanent = Invoke-RestMethod -Method Get -Uri `
        "$ManagementUrl/api/queues/%2F/ticket.ordering.inventory-results.v1" -Headers $headers
    Require ($permanent.durable -eq $true) 'Ordering Inventory result queue is not durable'

    Write-Host 'Inventory result outbox RabbitMQ smoke: PASS' -ForegroundColor Green
    Write-Host "Order: $orderId | Result message: $messageId | type: InventorySaleRejectedV1"
    Write-Host 'Confirmed in RabbitMQ probe queue and marked published in PostgreSQL; no sale was performed.'
}
finally {
    if ($probeCreated) {
        try {
            Invoke-RestMethod -Method Delete -Uri $queueUri -Headers $headers | Out-Null
        }
        catch {
            Write-Warning "Could not delete temporary RabbitMQ probe queue $probe : $_"
        }
    }
}
