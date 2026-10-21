# Runs against the LOCAL Compose deployment; requires a seeded demo event.
# Safe to repeat while at least two seats remain available.
# Windows PowerShell 5.1 and PowerShell 7 are supported.
param([string]$BaseUrl = 'http://localhost:8088')
$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$eventId = '11111111-1111-1111-1111-111111111111'

function Request([string]$method, [string]$path, $data = $null) {
    $request = @{
        Uri = "$BaseUrl$path"; Method = $method
        ErrorAction = 'Stop'; UseBasicParsing = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($script:buyerToken)) {
        $request.Headers = @{ Authorization = "Bearer $script:buyerToken" }
    }
    if ($null -ne $data) {
        $request.ContentType = 'application/json'
        $request.Body = ConvertTo-Json -InputObject $data -Depth 6 -Compress
    }
    try {
        $response = Invoke-WebRequest @request
        $contentType = [string]$response.Headers['Content-Type']
        $body = if ([string]::IsNullOrWhiteSpace($response.Content)) {
            $null
        } elseif ($contentType -match 'application/(json|[\w.-]+\+json)') {
            ConvertFrom-Json -InputObject $response.Content
        } else {
            # ASP.NET Core health endpoints return plain text (for example, Healthy).
            $response.Content
        }
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body = $body
            Location = [string]$response.Headers['Location']
        }
    } catch {
        $httpResponse = $_.Exception.Response
        if ($null -eq $httpResponse) { throw }
        # Expected non-2xx outcomes (such as 409) are still HTTP responses.
        return [pscustomobject]@{
            Status = [int]$httpResponse.StatusCode
            Body = $null
            Location = $null
        }
    }
}

function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw "Phase 1 smoke test failed: $message" }
}

$health = Request GET '/health/live'
Require ($health.Status -eq 200) 'health endpoint not healthy'
$events = Request GET '/events'
Require ($events.Status -eq 200) 'events endpoint failed'
Require (@($events.Body | Where-Object { $_.id -eq $eventId }).Count -eq 1) 'seed event not found'

$seatsUrl = "/events/$eventId/seats"
$seats = Request GET $seatsUrl
Require ($seats.Status -eq 200) 'seat-list endpoint failed'
$free = @($seats.Body | Where-Object { [int]$_.status -eq 0 } | Select-Object -First 2)
Require ($free.Count -eq 2) 'need two available demo seats; do not reset existing data without a backup'
$codes = @($free | ForEach-Object { $_.seatCode })
$buyer = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$script:buyerToken = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$holdKey = 'smoke-hold-' + [guid]::NewGuid().ToString('N')
$orderKey = 'smoke-order-' + [guid]::NewGuid().ToString('N')
$holdUrl = "/events/$eventId/holds"
$holdCommand = @{ buyerId = $buyer; idempotencyKey = $holdKey; seatCodes = $codes }

$created = Request POST $holdUrl $holdCommand
Require ($created.Status -eq 201) 'hold creation did not return 201'
Require (![string]::IsNullOrWhiteSpace($created.Location)) 'missing hold Location'
$readHold = Request GET $created.Location
Require ($readHold.Status -eq 200) 'hold Location did not resolve'
Require ($readHold.Body.id -eq $created.Body.id) 'hold Location returned a different hold'
$holdRetry = Request POST $holdUrl $holdCommand
Require ($holdRetry.Status -eq 200) 'hold replay did not return 200'
Require ($holdRetry.Body.id -eq $created.Body.id) 'hold replay changed ID'

$orderCommand = @{
    holdId = $created.Body.id; buyerId = $buyer; idempotencyKey = $orderKey
}
$order = Request POST '/orders' $orderCommand
Require ($order.Status -eq 201) 'checkout did not return 201'
$readOrder = Request GET $order.Location
Require ($readOrder.Status -eq 200) 'order Location did not resolve'
Require ($readOrder.Body.id -eq $order.Body.id) 'order Location returned a different order'
$orderRetry = Request POST '/orders' $orderCommand
Require ($orderRetry.Status -eq 200) 'order replay did not return 200'
Require ($orderRetry.Body.id -eq $order.Body.id) 'order replay changed ID'

$after = Request GET $seatsUrl
foreach ($code in $codes) {
    $seat = @($after.Body | Where-Object { $_.seatCode -eq $code })
    Require ($seat.Count -eq 1 -and [int]$seat[0].status -eq 2) "seat $code was not sold"
}
$otherBuyerCommand = @{
    buyerId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb' 
    idempotencyKey = ('smoke-conflict-' + [guid]::NewGuid().ToString('N'))
    seatCodes = @($codes[0])
}
$script:buyerToken = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username bob
$conflict = Request POST $holdUrl $otherBuyerCommand
Require ($conflict.Status -eq 409) 'sold seat should reject other buyer with 409'

Write-Host 'Phase 1 Compose HTTP smoke: PASS' -ForegroundColor Green
Write-Host "Event: $eventId | buyer: $buyer | sold seats: $($codes -join ', ')"
Write-Host "Hold: $($created.Body.id) | Order: $($order.Body.id)"
