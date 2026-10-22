# Phase 2.3r: simulate broker ACK followed by a lost Ordering DB publication mark.
# Re-arm one ALREADY CONFIRMED checkout fact belonging to THIS test's order.
# The original MessageId, JSON payload and routing remain unchanged.
# Only for a disposable, idle local Compose stack: sells one demo seat and stops ordering-saga.
[CmdletBinding()]
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091',
    [int]$TimeoutSeconds = 180,
    [switch]$AllowDisruption
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$ok, [string]$message) {
    if (-not $ok) { throw "Checkout fact ACK replay FAILED: $message" }
}
function Check-Exit([string]$step) {
    Require ($LASTEXITCODE -eq 0) "$step exited with code $LASTEXITCODE"
}
function Query-Db([string]$service, [string]$database, [string]$sql) {
    $lines = @(& docker compose exec -T $service psql -X -U ticketrush -d $database `
        -At -v ON_ERROR_STOP=1 -c $sql)
    Check-Exit "query on $service"
    return ($lines -join "`n").Trim()
}
function Running-Services {
    $list = @(& docker compose ps --status running --services)
    Check-Exit 'docker compose ps'
    return $list
}
function Wait-For([scriptblock]$predicate, [string]$description) {
    while ([DateTime]::UtcNow -lt $script:deadlineUtc) {
        if (& $predicate) { return }
        Start-Sleep -Seconds 1
    }
    throw "Checkout fact ACK replay FAILED: timed out waiting for $description"
}
function Checkout-Queue {
    return Invoke-RestMethod -Method Get `
        -Uri 'http://localhost:15672/api/queues/%2F/ticket.ordering.checkout-requested.v1' `
        -Headers $script:rabbitHeaders -TimeoutSec 5
}

Require $AllowDisruption.IsPresent 'use -AllowDisruption; this stops ordering-saga, rewrites one test outbox publication flag, and sells one demo seat'
Require ($TimeoutSeconds -ge 60 -and $TimeoutSeconds -le 300) 'TimeoutSeconds must be 60-300'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
$sagaStopped = $false
try {
    & docker compose config --quiet
    Check-Exit 'docker compose config'
    $running = @(Running-Services)
    $needed = @('api', 'inventory-api', 'inventory-consumer', 'inventory-result-outbox',
        'inventory-postgres', 'postgres', 'ordering-api', 'ordering-postgres',
        'ordering-outbox', 'ordering-saga', 'payment-worker', 'payment-postgres',
        'notification-worker', 'notification-postgres', 'rabbitmq')
    foreach ($service in $needed) {
        Require ($running -contains $service) "required service is not running: $service"
    }
    $preflight = Join-Path $PSScriptRoot 'Verify-Inventory-Database-Separation.ps1'
    Require (Test-Path -LiteralPath $preflight) 'missing Inventory separation preflight'
    & $preflight
    Require $? 'Inventory database separation preflight failed'

    $rabbitCredentials = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
    $script:rabbitHeaders = @{ Authorization = "Basic $rabbitCredentials" }
    $initialQueue = Checkout-Queue
    Require ([int]$initialQueue.messages_ready -eq 0 -and
        [int]$initialQueue.messages_unacknowledged -eq 0) `
        'checkout fact queue is not idle; complete other checkouts before running this drill'

    $eventId = '11111111-1111-1111-1111-111111111111'
    $buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $token = & (Join-Path $PSScriptRoot 'Get-Dev-BuyerToken.ps1') -Username alice
    Require (-not [string]::IsNullOrWhiteSpace([string]$token)) 'cannot obtain development buyer token'
    $headers = @{ Authorization = "Bearer $token" }
    $seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats" -TimeoutSec 10
    $seat = $seats | Where-Object { $_.status -eq 0 -or $_.status -eq 'Available' } | Select-Object -First 1
    Require ($null -ne $seat) 'no demo seat available; do not reset volumes automatically'
    $holdBody = @{
        buyerId = $buyerId
        idempotencyKey = 'ambiguous-hold-' + [Guid]::NewGuid().ToString('N')
        seatCodes = @([string]$seat.seatCode)
    } | ConvertTo-Json -Compress -Depth 5
    $hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
        -Headers $headers -ContentType 'application/json' -Body $holdBody -TimeoutSec 15
    Require ($null -ne $hold.id -and -not [string]::IsNullOrWhiteSpace([string]$hold.holdProof)) `
        'could not create an authenticated hold and hold proof'

    $script:deadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $checkoutBody = @{
        holdId = [string]$hold.id
        buyerId = $buyerId
        holdProof = [string]$hold.holdProof
        idempotencyKey = 'ambiguous-checkout-' + [Guid]::NewGuid().ToString('N')
    } | ConvertTo-Json -Compress
    $accepted = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
        -Headers $headers -ContentType 'application/json' -Body $checkoutBody -UseBasicParsing -TimeoutSec 15
    Require ([int]$accepted.StatusCode -eq 202) "expected HTTP 202, got $($accepted.StatusCode)"
    $receipt = $accepted.Content | ConvertFrom-Json
    $orderId = [string]$receipt.orderId
    $parsed = [Guid]::Empty
    Require ([Guid]::TryParse($orderId, [ref]$parsed)) 'invalid returned orderId'
    $statusUrl = "$OrderingUrl/checkouts/$orderId`?buyerId=$buyerId"
    Wait-For {
        try {
            $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers -TimeoutSec 5
            return ([string]$status.state -eq 'Completed' -or [string]$status.state -eq 'Failed')
        } catch { return $false }
    } 'terminal checkout before replay'
    $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers -TimeoutSec 5
    Require ([string]$status.state -eq 'Completed') `
        "checkout did not complete before replay: $($status.state) / $($status.failureReasonCode)"
    Wait-For {
        $receiptCount = Query-Db 'notification-postgres' 'ticketrush_notification' @"
SELECT COUNT(*) FROM notification_receipts WHERE order_id = '$orderId'::uuid AND outcome = 'Completed';
"@
        return ($receiptCount -eq '1')
    } 'completed Notification receipt'

    $initial = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT message_id::text || '|' || publish_attempts::text
FROM outbox_messages
WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1'
AND published_at_utc IS NOT NULL;
"@
    $parts = $initial.Split('|')
    Require ($parts.Count -eq 2) "original CheckoutRequestedV1 has no published ID and attempts: $initial"
    $messageId = $parts[0]
    $previousAttempts = 0
    Require ([Guid]::TryParse($messageId, [ref]$parsed)) 'invalid original outbox messageId'
    Require ([int]::TryParse($parts[1], [ref]$previousAttempts)) 'invalid original publish attempts'
    Write-Host "Checkout $orderId completed. Original fact $messageId was broker-confirmed."

    # Stop the consumer BEFORE replay so we can prove the original fact is
    # enqueued a second time, then resume processing deterministically.
    $sagaStopped = $true
    & docker compose stop -t 10 ordering-saga
    Check-Exit 'stopping ordering-saga'
    Require (@(Running-Services) -notcontains 'ordering-saga') 'ordering-saga is still running'

    # Simulate: RabbitMQ already accepted the event, but the publisher's DB
    # transaction rolled back and left published_at_utc NULL. Intentionally
    # modify ONLY the original row created by THIS drill. Not a new message ID.
    $updated = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
WITH rearmed AS (
  UPDATE outbox_messages
  SET published_at_utc = NULL, next_attempt_at_utc = NULL
  WHERE order_id = '$orderId'::uuid
    AND message_id = '$messageId'::uuid
    AND message_type = 'CheckoutRequestedV1'
    AND published_at_utc IS NOT NULL
  RETURNING 1
) SELECT COUNT(*) FROM rearmed;
"@
    Require ($updated -eq '1') 'could not re-arm the original CheckoutRequestedV1'

    Wait-For {
        $published = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT COUNT(*) FROM outbox_messages
WHERE message_id = '$messageId'::uuid AND order_id = '$orderId'::uuid
  AND published_at_utc IS NOT NULL AND publish_attempts > $previousAttempts;
"@
        return ($published -eq '1')
    } 'second broker-confirmed publication of the SAME checkout fact'
    Wait-For {
        $queue = Checkout-Queue
        return ([int]$queue.messages_ready -ge 1)
    } 'replayed checkout fact waiting in RabbitMQ while saga consumer is stopped'
    Write-Host 'Original CheckoutRequestedV1 was published twice; releasing waiting duplicate to saga worker...'

    & docker compose up -d --no-deps ordering-saga
    Check-Exit 'restarting ordering-saga'
    $sagaStopped = $false
    Wait-For {
        $queue = Checkout-Queue
        return ([int]$queue.messages_ready -eq 0 -and
                [int]$queue.messages_unacknowledged -eq 0)
    } 'Ordering saga consumer ACK of replayed checkout fact'
    # Wait for a second idle sample, avoiding a transient observation during reconnect.
    Start-Sleep -Seconds 2
    $queue = Checkout-Queue
    Require ([int]$queue.messages_ready -eq 0 -and
        [int]$queue.messages_unacknowledged -eq 0) 'checkout replay queue became nonempty again'

    # Check both business state and the number of commands emitted from that
    # single durable CheckoutRequested. These are scoped by this order ID.
    $ordering = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT (SELECT COUNT(*) FROM order_intents WHERE id = '$orderId'::uuid AND state = 1),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1'),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'PaymentAuthorizationRequestedV1'),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleRequestedV1'),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutNotificationRequestedV1'),
       (SELECT COUNT(*) FROM ordering_payment_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM ordering_result_inbox WHERE order_id = '$orderId'::uuid);
"@
    Require ($ordering -eq '1|1|1|1|1|1|1') "Ordering duplicated business effects: $ordering"
    $inventory = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT (SELECT COUNT(*) FROM inventory_sales WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_result_outbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM seats WHERE sold_order_id = '$orderId'::uuid AND status = 2);
"@
    Require ($inventory -eq '1|1|1|1') "Inventory duplicated sale or message effects: $inventory"
    $payment = Query-Db 'payment-postgres' 'ticketrush_payment' @"
SELECT (SELECT COUNT(*) FROM payment_receipts WHERE order_id = '$orderId'::uuid AND result_type = 'PaymentAuthorizedV1'),
       (SELECT COUNT(*) FROM payment_outbox WHERE order_id = '$orderId'::uuid);
"@
    Require ($payment -eq '1|1') "Payment effects changed after replay: $payment"
    $notification = Query-Db 'notification-postgres' 'ticketrush_notification' @"
SELECT COUNT(*) FROM notification_receipts WHERE order_id = '$orderId'::uuid AND outcome = 'Completed';
"@
    Require ($notification -eq '1') "Notification delivery receipts changed after replay: $notification"
    $legacy = Query-Db 'postgres' 'ticketrush' @"
SELECT COUNT(*) FROM orders WHERE id = '$orderId'::uuid OR hold_id = '$($hold.id)'::uuid;
"@
    Require ($legacy -eq '0') "Legacy Booking database received order: $legacy"
    $final = Invoke-RestMethod -Uri $statusUrl -Headers $headers -TimeoutSec 5
    Require ([string]$final.state -eq 'Completed') "checkout left Completed after replay: $($final.state)"

    Write-Host 'Checkout fact ambiguous-ACK replay: PASS' -ForegroundColor Green
    Write-Host "Order: $orderId | Original MessageId: $messageId | Publish attempts: $previousAttempts -> retry | Sales: 1 | Notifications: 1"
    Write-Host 'Note: this replays an already confirmed event by resetting its DB mark. It does not force a real network-level ACK loss.'
}
finally {
    if ($sagaStopped) {
        Write-Warning 'Restoring Ordering saga worker after replay drill failure...'
        & docker compose up -d --no-deps ordering-saga
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Automatic Ordering saga restart failed; run: docker compose up -d --no-deps ordering-saga'
        }
    }
    Pop-Location
}
