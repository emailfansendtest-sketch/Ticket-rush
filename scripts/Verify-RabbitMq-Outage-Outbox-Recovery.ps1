# Phase 2.3q: fail/recover the RabbitMQ broker while Ordering's outbox is authoritative.
# A real authenticated checkout is committed while RabbitMQ is stopped; the outbox
# must record a failed publish and later deliver the ORIGINAL committed message.
# This is intentionally disruptive and sells ONE demo seat. LOCAL DISPOSABLE STACK ONLY.
[CmdletBinding()]
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091',
    [int]$TimeoutSeconds = 240,
    [switch]$AllowDisruption
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "RabbitMQ outage recovery FAILED: $message" }
}
function Check-Exit([string]$step) {
    Require ($LASTEXITCODE -eq 0) "$step exited with code $LASTEXITCODE"
}
function Query-Db([string]$service, [string]$db, [string]$sql) {
    $output = @(& docker compose exec -T $service psql -X -U ticketrush -d $db `
        -At -v ON_ERROR_STOP=1 -c $sql)
    Check-Exit "SQL query on $service"
    return ($output -join "`n").Trim()
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
    throw "RabbitMQ outage recovery FAILED: timed out waiting for $description"
}

Require $AllowDisruption.IsPresent `
    'specify -AllowDisruption; this test stops RabbitMQ, exercises real payments (simulated), and sells one demo seat'
Require ($TimeoutSeconds -ge 60 -and $TimeoutSeconds -le 300) `
    'TimeoutSeconds must be 60-300 (below the 10-minute hold lifetime)'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
$brokerStopped = $false
try {
    & docker compose config --quiet
    Check-Exit 'docker compose config'
    $running = @(Running-Services)
    $needed = @('api', 'inventory-api', 'inventory-consumer', 'inventory-result-outbox',
        'inventory-postgres', 'postgres', 'ordering-api', 'ordering-postgres',
        'ordering-outbox', 'ordering-saga', 'payment-worker', 'payment-postgres',
        'notification-worker', 'notification-postgres', 'rabbitmq')
    foreach ($service in $needed) {
        Require ($running -contains $service) "required Compose service is not running: $service"
    }
    $separationCheck = Join-Path $PSScriptRoot 'Verify-Inventory-Database-Separation.ps1'
    Require (Test-Path -LiteralPath $separationCheck) 'missing Inventory database separation preflight'
    & $separationCheck
    Require $? 'Inventory database separation preflight failed'

    # Use a real hold/proof so that admission commits a genuine checkout + outbox
    # event. The Booking and Ordering HTTP APIs do not require RabbitMQ to accept it.
    $eventId = '11111111-1111-1111-1111-111111111111'
    $buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
    $token = & (Join-Path $PSScriptRoot 'Get-Dev-BuyerToken.ps1') -Username alice
    Require (-not [string]::IsNullOrWhiteSpace([string]$token)) 'cannot obtain development buyer token'
    $headers = @{ Authorization = "Bearer $token" }
    $seats = Invoke-RestMethod -Uri "$BookingUrl/events/$eventId/seats"
    $seat = $seats | Where-Object { $_.status -eq 0 -or $_.status -eq 'Available' } | Select-Object -First 1
    Require ($null -ne $seat) 'no demo seat available; do not reset volumes automatically'
    $holdBody = @{
        buyerId = $buyerId
        idempotencyKey = 'broker-outage-hold-' + [guid]::NewGuid().ToString('N')
        seatCodes = @([string]$seat.seatCode)
    } | ConvertTo-Json -Compress -Depth 5
    $hold = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
        -Headers $headers -ContentType 'application/json' -Body $holdBody
    Require ($null -ne $hold.id -and -not [string]::IsNullOrWhiteSpace([string]$hold.holdProof)) `
        'hold was not created with a signed hold proof'
    Write-Host "Reserved $($seat.seatCode). Stopping RabbitMQ before checkout admission..."

    # Start a single *global* timeout after acquiring the hold, to avoid an
    # expired reservation after a long sequence of independent polling loops.
    $script:deadlineUtc = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $brokerStopped = $true # arm finally even if docker stops broker then errors
    & docker compose stop -t 10 rabbitmq
    Check-Exit 'stopping RabbitMQ'
    Require (@(Running-Services) -notcontains 'rabbitmq') 'RabbitMQ is still running'

    $checkoutBody = @{
        holdId = [string]$hold.id
        buyerId = $buyerId
        holdProof = [string]$hold.holdProof
        idempotencyKey = 'broker-outage-checkout-' + [guid]::NewGuid().ToString('N')
    } | ConvertTo-Json -Compress
    $accepted = Invoke-WebRequest -Method Post -Uri "$OrderingUrl/checkouts" `
        -Headers $headers -ContentType 'application/json' -Body $checkoutBody -UseBasicParsing
    Require ([int]$accepted.StatusCode -eq 202) `
        "checkout must be durably accepted with 202 while broker is down, got $($accepted.StatusCode)"
    $receipt = $accepted.Content | ConvertFrom-Json
    $orderId = [string]$receipt.orderId
    $parsedOrderId = [Guid]::Empty
    Require ([Guid]::TryParse($orderId, [ref]$parsedOrderId)) 'Ordering returned an invalid orderId'
    $statusUrl = "$OrderingUrl/checkouts/$orderId`?buyerId=$buyerId"
    Write-Host "Checkout $orderId committed while broker is offline."

    # Observe a persisted failed attempt; simply seeing Pending is insufficient
    # evidence because the publisher may not have tried to reach the broker yet.
    Wait-For {
        $pending = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT COUNT(*) FROM outbox_messages
WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1'
  AND published_at_utc IS NULL AND publish_attempts >= 1 AND last_error IS NOT NULL;
"@
        return ($pending -eq '1')
    } 'unpublished CheckoutRequestedV1 with persisted failed publish attempt'

    $before = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT (SELECT COUNT(*) FROM order_intents WHERE id = '$orderId'::uuid AND state = 0),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1' AND published_at_utc IS NULL),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'PaymentAuthorizationRequestedV1');
"@
    Require ($before -eq '1|1|0') "Ordering was not durably Pending/unpublished: $before"
    $inventoryBefore = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT (SELECT COUNT(*) FROM inventory_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_sales WHERE order_id = '$orderId'::uuid);
"@
    Require ($inventoryBefore -eq '0|0') "Inventory wrote before broker recovered: $inventoryBefore"
    $paymentBefore = Query-Db 'payment-postgres' 'ticketrush_payment' @"
SELECT COUNT(*) FROM payment_receipts WHERE order_id = '$orderId'::uuid;
"@
    Require ($paymentBefore -eq '0') "Payment wrote before broker recovered: $paymentBefore"
    $pendingHttp = Invoke-RestMethod -Uri $statusUrl -Headers $headers
    Require ([string]$pendingHttp.state -eq 'Pending') `
        "checkout left Pending during broker outage: $($pendingHttp.state)"
    Write-Host 'PASS: Ordering persisted an unpublished outbox event and recorded a failed broker attempt.'

    # Restart ONLY RabbitMQ. Consumers and outbox workers must recover themselves
    # without manual restarts; forcing their recreation would mask a real bug.
    & docker compose up -d --no-deps rabbitmq
    Check-Exit 'restarting RabbitMQ'
    $brokerStopped = $false
    $rabbitBasicAuth = [Convert]::ToBase64String(
        [Text.Encoding]::ASCII.GetBytes('ticketrush:localdevonly'))
    Wait-For {
        try {
            $null = Invoke-RestMethod -Method Get -Uri 'http://localhost:15672/api/overview' `
                -Headers @{ Authorization = "Basic $rabbitBasicAuth" } -TimeoutSec 5
            return $true
        }
        catch { return $false }
    } 'RabbitMQ management API after restart'

    Wait-For {
        $published = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT COUNT(*) FROM outbox_messages
WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1'
  AND published_at_utc IS NOT NULL AND publish_attempts >= 2 AND last_error IS NULL;
"@
        return ($published -eq '1')
    } 'retry and broker confirmation of the ORIGINAL outbox event'
    Wait-For {
        try {
            $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers -TimeoutSec 5
            return ([string]$status.state -eq 'Completed' -or [string]$status.state -eq 'Failed')
        } catch { return $false }
    } 'terminal checkout after broker recovery'
    $status = Invoke-RestMethod -Uri $statusUrl -Headers $headers
    Require ([string]$status.state -eq 'Completed') `
        "checkout did not complete after broker recovery (state=$($status.state), reason=$($status.failureReasonCode))"

    # Verify independent durable outcomes. These are per-order assertions, not
    # global counts, and remain valid even when earlier smoke tests sold seats.
    $ordering = Query-Db 'ordering-postgres' 'ticketrush_ordering' @"
SELECT (SELECT COUNT(*) FROM order_intents WHERE id = '$orderId'::uuid AND state = 1),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'CheckoutRequestedV1' AND published_at_utc IS NOT NULL),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'PaymentAuthorizationRequestedV1' AND published_at_utc IS NOT NULL),
       (SELECT COUNT(*) FROM outbox_messages WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleRequestedV1' AND published_at_utc IS NOT NULL),
       (SELECT COUNT(*) FROM ordering_payment_inbox WHERE order_id = '$orderId'::uuid AND message_type = 'PaymentAuthorizedV1'),
       (SELECT COUNT(*) FROM ordering_result_inbox WHERE order_id = '$orderId'::uuid AND message_type = 'InventorySaleCommittedV1');
"@
    Require ($ordering -eq '1|1|1|1|1|1') "Ordering duplicated or lost saga records: $ordering"
    $inventory = Query-Db 'inventory-postgres' 'ticketrush_inventory' @"
SELECT (SELECT COUNT(*) FROM inventory_inbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_result_outbox WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM inventory_sales WHERE order_id = '$orderId'::uuid),
       (SELECT COUNT(*) FROM seats WHERE sold_order_id = '$orderId'::uuid AND status = 2);
"@
    Require ($inventory -eq '1|1|1|1') "Inventory missing sale or duplicate effects: $inventory"
    $payment = Query-Db 'payment-postgres' 'ticketrush_payment' @"
SELECT (SELECT COUNT(*) FROM payment_receipts WHERE order_id = '$orderId'::uuid AND result_type = 'PaymentAuthorizedV1'),
       (SELECT COUNT(*) FROM payment_outbox WHERE order_id = '$orderId'::uuid AND published_at_utc IS NOT NULL);
"@
    Require ($payment -eq '1|1') "Payment effects duplicated or missing: $payment"
    Wait-For {
        $notification = Query-Db 'notification-postgres' 'ticketrush_notification' @"
SELECT COUNT(*) FROM notification_receipts
WHERE order_id = '$orderId'::uuid AND outcome = 'Completed';
"@
        return ($notification -eq '1')
    } 'single terminal Notification receipt'
    $legacy = Query-Db 'postgres' 'ticketrush' @"
SELECT COUNT(*) FROM orders
WHERE id = '$orderId'::uuid OR hold_id = '$($hold.id)'::uuid;
"@
    Require ($legacy -eq '0') "legacy Booking DB unexpectedly received the checkout: $legacy"

    Write-Host 'RabbitMQ outage + transactional outbox recovery: PASS' -ForegroundColor Green
    Write-Host "Order: $orderId | Seat: $($seat.seatCode) | Outbox retry confirmed | Sales: 1 | Notifications: 1"
    Write-Host 'Note: validates failed publish/retry; ambiguous ACK or exactly-once transport is not claimed.'
}
finally {
    # Always attempt to restore the broker even after a failed pre-recovery check.
    if ($brokerStopped) {
        Write-Warning 'Restoring RabbitMQ after failed outage drill...'
        & docker compose up -d --no-deps rabbitmq
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'RabbitMQ restart failed; manually run: docker compose up -d --no-deps rabbitmq'
        }
    }
    Pop-Location
}
