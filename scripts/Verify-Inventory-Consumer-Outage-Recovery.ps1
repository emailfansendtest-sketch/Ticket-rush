# Phase 2.3p: live distributed recovery drill after Inventory database separation.
# PURPOSE: stop the Inventory command consumer, submit a real authenticated checkout,
# verify that the Inventory command is durably broker-published while the checkout
# stays Pending, restart the consumer, and prove there is exactly one sale.
# Also republishes ONLY this new test order's Inventory command to exercise
# idempotent consumer replay and result/outbox re-arming.
# WARNING: disruptive; requires disposable local development data; sells one demo seat.
[CmdletBinding()]
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091',
    [int]$TimeoutSeconds = 120,
    [switch]$AllowDisruption
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$ok, [string]$message) {
    if (-not $ok) { throw "Inventory outage recovery FAILED: $message" }
}
function Check-Exit([string]$step) {
    Require ($LASTEXITCODE -eq 0) "$step exited with code $LASTEXITCODE"
}
function Query-Db([string]$service, [string]$db, [string]$sql) {
    $lines = @(& docker compose exec -T $service psql -X -U ticketrush -d $db -At `
        -v ON_ERROR_STOP=1 -c $sql)
    Check-Exit "SQL on $service"
    return ($lines -join "`n").Trim()
}
function Wait-For([scriptblock]$predicate, [string]$description) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $predicate) { return }
        Start-Sleep -Seconds 1
    }
    throw "Inventory outage recovery FAILED: timed out waiting for $description"
}
function Running-Services {
    $lines = @(& docker compose ps --status running --services)
    Check-Exit 'docker compose ps'
    return $lines
}

Require $AllowDisruption.IsPresent 'explicitly specify -AllowDisruption (this test stops a worker and sells one demo seat)'
Require ($TimeoutSeconds -ge 30 -and $TimeoutSeconds -le 300) 'TimeoutSeconds must be between 30 and 300'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
$consumerStopped = $false
try {
    & docker compose config --quiet
    Check-Exit 'docker compose config'
    $running = @(Running-Services)
    $necessary = @('api', 'inventory-api', 'inventory-consumer',
        'inventory-result-outbox', 'inventory-postgres', 'postgres',
        'ordering-api', 'ordering-postgres', 'ordering-outbox', 'ordering-saga',
        'payment-worker', 'payment-postgres', 'notification-worker', 'notification-postgres',
        'rabbitmq')
    foreach ($service in $necessary) {
        Require ($running -contains $service) "required Compose service not running: $service"
    }
    Require (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Get-Dev-BuyerToken.ps1')) `
        'missing Get-Dev-BuyerToken.ps1'
    # This drill must not be used against a pre-cutover instance.
    & (Join-Path $PSScriptRoot 'Verify-Inventory-Database-Separation.ps1')
    Require $? 'Inventory database separation preflight failed'

    $eventId = '11111111-1111-1111-1111-111111111111'
    $buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $token = & (Join-Path $PSScriptRoot 'Get-Dev-BuyerToken.ps1') -Username alice
    Require (-not [string]::IsNullOrWhiteSpace([string]$token)) 'could not obtain Alice development bearer token'
    $headers = @{ Authorization = "Bearer $token" }

    # Reserve before stopping the consumer so no hold duration is spent on setup.
    $seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
    $seat = $seats | Where-Object {
        $_.status -eq 0 -or $_.status -eq 'Available'
    } | Select-Object -First 1
    Require ($null -ne $seat) 'no available demo seats remain; do not reset persisted volumes automatically'

    $holdKey = 'outage-hold-' + [Guid]::NewGuid().ToString('N')
    $holdBody = @{
        buyerId = $buyerId
        idempotencyKey = $holdKey
        seatCodes = @([string]$seat.seatCode)
    } | ConvertTo-Json -Compress -Depth 5
    $hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
        -Headers $headers -ContentType 'application/json' -Body $holdBody
    Require ($null -ne $hold.id -and -not [string]::IsNullOrWhiteSpace([string]$hold.holdProof)) `
        'could not create a signed Inventory hold'

    # Stop the consumer, NOT the broker or database. The durable command will
    # accumulate in RabbitMQ rather than getting applied to Inventory immediately.
    # Arm the finally restoration before invoking stop, in case Docker stops
    # the container but returns an error after the stop operation.
    $consumerStopped = $true
    & docker compose stop -t 10 inventory-consumer
    Check-Exit 'stopping inventory-consumer'
    $running = @(Running-Services)
    Require ($running -notcontains 'inventory-consumer') 'Inventory consumer is still running'

    $checkoutKey = 'outage-checkout-' + [Guid]::NewGuid().ToString('N')
    $checkoutBody = @{
        holdId = [string]$hold.id
        buyerId = $buyerId
        holdProof = [string]$hold.holdProof
        idempotencyKey = $checkoutKey
    } | ConvertTo-Json -Compress
    $accepted = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
        -Headers $headers -ContentType 'application/json' -Body $checkoutBody -UseBasicParsing
    Require ([int]$accepted.StatusCode -eq 202) "new checkout must return HTTP 202, got $($accepted.StatusCode)"
    $checkout = $accepted.Content | ConvertFrom-Json
    $orderId = [string]$checkout.orderId
    $parsedOrder = [Guid]::Empty
    Require ([Guid]::TryParse($orderId, [ref]$parsedOrder)) 'checkout response contained no order GUID'
    $statusUrl = "$OrderingUrl/checkouts/$orderId`?buyerId=$buyerId"
    Write-Host "Inventory paused. Checkout accepted: $orderId; waiting for broker-confirmed command..."

    Wait-For {
        $ready = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT COUNT(*) FROM outbox_messages
WHERE order_id = '$orderId'::uuid
  AND message_type = 'InventorySaleRequestedV1'
  AND published_at_utc IS NOT NULL;
"@
        return ($ready -eq '1')
    } 'Ordering InventorySaleRequestedV1 outbox broker confirmation'

    $waiting = Invoke-RestMethod -Uri $statusUrl -Headers $headers
    Require ([string]$waiting.state -eq 'Pending') `
        "checkout advanced without Inventory consumer (state=$($waiting.state))"
    $before = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT (SELECT COUNT(*) FROM inventory_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_sales WHERE order_id = '$orderId'::uuid);
"@
    # psql -At defaults to | separator, so the expected output is 0|0.
    Require ($before -eq '0|0') "unexpected pre-resume Inbox/Sale counts: $before"
    Write-Host 'PASS: Inventory command broker-confirmed; order pending; no Inventory write.'

    & docker compose up -d --no-deps inventory-consumer
    Check-Exit 'restarting inventory-consumer'
    $consumerStopped = $false

    Wait-For {
        $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers
        return ([string]$status.state -eq 'Completed' -or [string]$status.state -eq 'Failed')
    } 'terminal checkout after Inventory consumer restart'
    $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers
    Require ([string]$status.state -eq 'Completed') `
        "checkout failed after recovery (state=$($status.state); reason=$($status.failureReasonCode))"

    # Publish the EXACT same Inventory command a second time; do not insert a
    # different command or change identity. The consumer must deduplicate it.
    $beforeAttempts = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT publish_attempts FROM outbox_messages
WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleRequestedV1';
"@
    $attempts = 0
    Require ([int]::TryParse($beforeAttempts, [ref]$attempts)) 'could not read original command publish attempts'
    $rearmed = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
WITH changed AS (
  UPDATE outbox_messages SET published_at_utc = NULL, next_attempt_at_utc = NULL
  WHERE order_id = '$orderId'::uuid
    AND message_type = 'InventorySaleRequestedV1'
    AND published_at_utc IS NOT NULL
  RETURNING 1
) SELECT COUNT(*) FROM changed;
"@
    Require ($rearmed -eq '1') 'could not re-arm the original, already-published Inventory command'
    Write-Host 'Original Inventory command rearmed for one deliberate duplicate delivery...'
    Wait-For {
        $republished = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT COUNT(*) FROM outbox_messages
WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleRequestedV1'
  AND published_at_utc IS NOT NULL AND publish_attempts > $attempts;
"@
        return ($republished -eq '1')
    } 'second broker confirmation for original Inventory command'

    # Inventory replay uses the already processed inbox receipt to re-arm
    # the ORIGINAL result. Wait for the re-published result confirmation too.
    Wait-For {
        $result = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT COUNT(*) FROM inventory_result_outbox
WHERE order_id = '$orderId'::uuid
  AND message_type = 'InventorySaleCommittedV1'
  AND published_at_utc IS NOT NULL AND publish_attempts >= 2;
"@
        return ($result -eq '1')
    } 'Inventory duplicate replay and result re-publication'

    # A broker confirmation alone is not proof that Ordering ACKed the duplicate.
    # Wait for the local ordering-result queue to drain. This assumes an isolated
    # development Compose stack without unrelated background checkout traffic.
    $rabbitCredentials = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
    Wait-For {
        $queue = Invoke-RestMethod -Method Get `
            -Uri 'http://localhost:15672/api/queues/%2F/ticket.ordering.inventory-results.v1' `
            -Headers @{ Authorization = "Basic $rabbitCredentials" }
        return ([int]$queue.messages_ready -eq 0 -and
                [int]$queue.messages_unacknowledged -eq 0)
    } 'Ordering ACK of re-published Inventory result'

    $inventory = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT (SELECT COUNT(*) FROM inventory_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_result_outbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_sales WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM seats WHERE sold_order_id = '$orderId'::uuid AND status = 2);
"@
    Require ($inventory -eq '1|1|1|1') "Inventory created duplicates or did not sell one seat: $inventory"

    $ordering = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT (SELECT COUNT(*) FROM order_intents WHERE id = '$orderId'::uuid AND state = 1),
       (SELECT COUNT(*) FROM ordering_result_inbox WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleCommittedV1'),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleRequestedV1');
"@
    Require ($ordering -eq '1|1|1') "Ordering final state or idempotency changed on duplicate: $ordering"

    $payment = Query-Db 'payment-postgres' 'ticketrush_payment' @"
SELECT COUNT(*) FROM payment_receipts WHERE order_id = '$orderId'::uuid
AND result_type = 'PaymentAuthorizedV1';
"@
    Require ($payment -eq '1') "Payment authorization duplicated or missing: $payment"

    Wait-For {
        $notification = Query-Db 'notification-postgres' 'ticketrush_notification' @"
SELECT COUNT(*) FROM notification_receipts WHERE order_id = '$orderId'::uuid AND outcome = 'Completed';
"@
        return ($notification -eq '1')
    } 'single completed checkout notification'

    $legacy = Query-Db 'postgres' 'ticketrush' @"
SELECT COUNT(*) FROM orders WHERE id = '$orderId'::uuid OR hold_id = '$($hold.id)'::uuid;
"@
    Require ($legacy -eq '0') "legacy Booking database unexpectedly contains new order: $legacy"
    $final = Invoke-RestMethod -Uri $statusUrl -Headers $headers
    Require ([string]$final.state -eq 'Completed') 'order left Completed state after duplicate replay'
    Write-Host 'Inventory consumer outage and duplicate replay: PASS' -ForegroundColor Green
    Write-Host "Order: $orderId | Seat: $($seat.seatCode) | Inventory sales: 1 | Notification receipts: 1"
    Write-Host 'Recovery uses InventoryDb; legacy Booking DB remained unchanged for this order.'
}
finally {
    # Restore the service even when the test throws or the checkout remains Pending.
    if ($consumerStopped) {
        Write-Warning 'Restoring inventory-consumer after failure...'
        & docker compose up -d --no-deps inventory-consumer
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'Automatic restart failed; run: docker compose up -d --no-deps inventory-consumer'
        }
    }
    Pop-Location
}
