# Phase 2.3o.2b: prove the public Booking API no longer sells seats through
# the legacy synchronous transaction, while the async Ordering API stays up.
param(
    [string]$BookingUrl = 'http://localhost:8088',
    [string]$OrderingUrl = 'http://localhost:8091'
)
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
$OrderingUrl = $OrderingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Legacy checkout retirement FAILED: $message" }
}
$buyer = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
Require ($LASTEXITCODE -eq 0) 'could not acquire buyer token'
$headers = @{ Authorization = "Bearer $token" }
function Expect-Gone($method, $uri, $body = $null) {
    try {
        if ($null -eq $body) {
            $result = Invoke-WebRequest -Method $method -Uri $uri -Headers $headers -UseBasicParsing -ErrorAction Stop
        } else {
            $result = Invoke-WebRequest -Method $method -Uri $uri -Headers $headers -ContentType 'application/json' -Body $body -UseBasicParsing -ErrorAction Stop
        }
        throw "unexpected successful response from $uri ($($result.StatusCode))"
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
        Require ($status -eq 410) "expected HTTP 410 from $uri; got $status"
    }
}
# Any authenticated request to legacy route must be rejected without updating seats.
Expect-Gone 'POST' "$BookingUrl/orders" '{"holdId":"11111111-1111-1111-1111-111111111111","buyerId":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","idempotencyKey":"deprecated"}'
Expect-Gone 'GET' "$BookingUrl/orders/11111111-1111-1111-1111-111111111111"
$health = Invoke-WebRequest -Uri "$OrderingUrl/health/live" -UseBasicParsing
Require ([int]$health.StatusCode -eq 200) 'Ordering API unavailable'
$running = @(docker compose ps --status running --services)
Require ($LASTEXITCODE -eq 0) 'cannot read Compose services'
Require ($running -contains 'inventory-api') 'separate Inventory API is not running'
# The gateway must have no BookingDb connection string in its environment.
$gatewayEnv = docker compose exec -T api printenv
Require ($LASTEXITCODE -eq 0) 'cannot inspect Booking API environment'
Require (-not [bool]($gatewayEnv | Select-String 'ConnectionStrings__BookingDb=')) 'gateway still has Booking database credentials'
Write-Host 'Legacy synchronous checkout retired: PASS' -ForegroundColor Green
