param(
    [string]$ManagementUrl = 'http://localhost:15672',
    [string]$RabbitUser = 'ticketrush',
    [string]$RabbitPassword = 'localdevonly'
)
$ErrorActionPreference = 'Stop'
$auth = 'Basic ' + [Convert]::ToBase64String(
    [Text.Encoding]::ASCII.GetBytes("${RabbitUser}:${RabbitPassword}"))
$headers = @{ Authorization = $auth }
$api = $ManagementUrl.TrimEnd('/') + '/api'
$exchange = 'ticket.events'
$queueName = 'ticket.payment.verify.' + [Guid]::NewGuid().ToString('N')
$escapedQueue = [Uri]::EscapeDataString($queueName)
$probeUri = "$api/queues/%2F/$escapedQueue"

function Post-Json([string]$url, [object]$data) {
    $json = ConvertTo-Json -InputObject $data -Depth 12 -Compress
    return Invoke-RestMethod -Method Post -Uri $url -Headers $headers `
        -ContentType 'application/json' -Body $json
}
function Query-Payment([string]$sql) {
    $answer = & docker compose exec -T payment-postgres psql -U ticketrush `
        -d ticketrush_payment -A -t -v ON_ERROR_STOP=1 -c $sql
    if ($LASTEXITCODE -ne 0) { throw 'Payment PostgreSQL query failed.' }
    return (($answer | Out-String).Trim())
}
function Wait-Until([scriptblock]$check, [string]$label, [int]$seconds = 60) {
    for ($i = 0; $i -lt $seconds; $i++) {
        if (& $check) { return }
        Start-Sleep -Seconds 1
    }
    throw "Timed out: $label. Check docker compose logs payment-worker payment-migrate."
}

# The Payment worker declares its command queue when it connects.
Wait-Until {
    try {
        $q = Invoke-RestMethod -Method Get -Uri "$api/queues/%2F/ticket.payment.authorize-requested.v1" -Headers $headers
        return ($null -ne $q)
    } catch { return $false }
} 'Payment command queue was not declared'

$probeCreated = $false
try {
    # RabbitMQ 4 disallows transient non-exclusive queues. Use a durable probe,
    # explicitly deleted in the finally block.
    $probeConfig = @{ durable = $true; auto_delete = $false; arguments = @{} }
    $json = ConvertTo-Json -InputObject $probeConfig -Depth 5 -Compress
    Invoke-RestMethod -Method Put -Uri $probeUri -Headers $headers `
        -ContentType 'application/json' -Body $json | Out-Null
    $probeCreated = $true
    foreach ($routing in @('payment.authorized.v1', 'payment.declined.v1')) {
        Post-Json "$api/bindings/%2F/e/$exchange/q/$escapedQueue" `
            @{ routing_key = $routing; arguments = @{} } | Out-Null
    }

    $buyerId = [Guid]::NewGuid().ToString('D')
    $entries = @(
        @{ orderId = [Guid]::NewGuid().ToString('D'); messageId = [Guid]::NewGuid().ToString('D'); simulateDecline = $false; expectedType = 'PaymentAuthorizedV1' },
        @{ orderId = [Guid]::NewGuid().ToString('D'); messageId = [Guid]::NewGuid().ToString('D'); simulateDecline = $true; expectedType = 'PaymentDeclinedV1' }
    )
    foreach ($entry in $entries) {
        $command = @{
            messageId = $entry.messageId
            orderId = $entry.orderId
            buyerId = $buyerId
            amountMinor = 1250
            currency = 'EUR'
            simulateDecline = $entry.simulateDecline
            requestedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        }
        $body = ConvertTo-Json -InputObject $command -Depth 6 -Compress
        $published = Post-Json "$api/exchanges/%2F/$exchange/publish" @{
            routing_key = 'payment.authorize-requested.v1'
            payload = $body
            payload_encoding = 'string'
            properties = @{ type = 'PaymentAuthorizationRequestedV1'; message_id = $entry.messageId; delivery_mode = 2 }
        }
        if (-not $published.routed) { throw 'Payment authorization command was not routed.' }
    }

    $observed = @{}
    Wait-Until {
        $raw = Invoke-WebRequest -UseBasicParsing -Method Post `
            -Uri "$api/queues/%2F/$escapedQueue/get" -Headers $headers `
            -ContentType 'application/json' `
            -Body '{"count":20,"ackmode":"ack_requeue_false","encoding":"auto","truncate":50000}'
        $messages = ConvertFrom-Json -InputObject $raw.Content
        foreach ($item in $messages) {
            $payload = ConvertFrom-Json -InputObject $item.payload
            $observed[$payload.orderId] = $item
        }
        return ($observed.Count -ge 2)
    } 'Both Payment outcomes were not published' 90

    foreach ($entry in $entries) {
        if (-not $observed.ContainsKey($entry.orderId)) {
            throw "No Payment outcome for order $($entry.orderId)"
        }
        $item = $observed[$entry.orderId]
        if ($item.properties.type -ne $entry.expectedType) {
            throw "Payment outcome type mismatch for $($entry.orderId)"
        }
        $payload = ConvertFrom-Json -InputObject $item.payload
        if ($payload.messageId -ne $item.properties.message_id) {
            throw 'Payment result MessageId header mismatch.'
        }
        $dbRows = Query-Payment "SELECT COUNT(*) FROM payment_receipts WHERE order_id = '$($entry.orderId)';"
        if ($dbRows -ne '1') { throw 'Payment receipt count was not one.' }
        $publishedRows = Query-Payment "SELECT COUNT(*) FROM payment_outbox WHERE order_id = '$($entry.orderId)' AND published_at_utc IS NOT NULL AND message_id = '$($payload.messageId)';"
        if ($publishedRows -ne '1') { throw 'Payment result was not confirmed in PostgreSQL.' }
    }
    Write-Host 'Payment authorization simulation RabbitMQ smoke: PASS'
    foreach ($entry in $entries) {
        Write-Host "Order: $($entry.orderId) | result: $($entry.expectedType)"
    }
    Write-Host 'Both results broker-published and persisted; no money captured and no seat sold.'
}
finally {
    if ($probeCreated) {
        try {
            Invoke-RestMethod -Method Delete -Uri $probeUri -Headers $headers | Out-Null
        } catch {
            Write-Warning "Could not delete the temporary RabbitMQ probe queue $queueName"
        }
    }
}
