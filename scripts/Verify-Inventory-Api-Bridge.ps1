# Phase 2.3o.2a: exercise the separate Inventory HTTP host through the Booking API.
# Uses one currently available demo seat; no destructive resets.
param([string]$BookingUrl = 'http://localhost:8088')
$ErrorActionPreference = 'Stop'
$BookingUrl = $BookingUrl.TrimEnd('/')
function Require($condition, $message) {
    if (-not $condition) { throw "Inventory API bridge smoke FAILED: $message" }
}
$eventId = '11111111-1111-1111-1111-111111111111'
$buyerId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
$token = & "$PSScriptRoot/Get-Dev-BuyerToken.ps1" -Username alice
$headers = @{ Authorization = "Bearer $token" }

# Cross-service continuity: the Inventory HTTP container must be running.
$services = docker compose ps --status running --services
Require ($LASTEXITCODE -eq 0) 'cannot read running Compose services'
Require ($services -contains 'inventory-api') 'inventory-api is not running'
$seats = Invoke-RestMethod "$BookingUrl/events/$eventId/seats"
$seat = $seats | Where-Object { $_.status -eq 0 -or $_.status -eq 'Available' } | Select-Object -First 1
Require ($null -ne $seat) 'no available demo seats remain'

$key = 'inventory-api-bridge-' + [Guid]::NewGuid().ToString('N')
$payload = @{
    buyerId = $buyerId; idempotencyKey = $key; seatCodes = @([string]$seat.seatCode)
} | ConvertTo-Json -Compress -Depth 5
$created = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $headers -ContentType 'application/json' -Body $payload
Require (-not [string]::IsNullOrWhiteSpace([string]$created.holdProof)) 'public Booking API did not sign hold'
Require ([string]$created.buyerId -eq $buyerId) 'hold ownership mismatch'
$replayed = Invoke-RestMethod -Method Post -Uri "$BookingUrl/events/$eventId/holds" `
    -Headers $headers -ContentType 'application/json' -Body $payload
Require ([string]$created.id -eq [string]$replayed.id) 'idempotency replay changed hold ID'
Require ([string]$created.holdProof -eq [string]$replayed.holdProof) 'idempotency replay changed hold proof'
$readBack = Invoke-RestMethod -Uri "$BookingUrl/holds/$($created.id)" -Headers $headers
Require ([string]$readBack.id -eq [string]$created.id) 'public hold read did not reach Inventory API'
Require ($null -eq $readBack.holdProof) 'public hold GET leaked a signed proof'
Write-Host 'Inventory HTTP API bridge smoke: PASS' -ForegroundColor Green
Write-Host "Hold: $($created.id) | Seat: $($seat.seatCode)"
