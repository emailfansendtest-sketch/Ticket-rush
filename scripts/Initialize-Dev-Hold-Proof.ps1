# Create a local random key shared by Booking and Ordering HTTP APIs.
# The .env file is ignored by Git and must never be committed.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$destination = Join-Path $root '.env'
$name = 'HOLD_PROOF_KEY_BASE64'
if (Test-Path $destination) {
    $existing = Get-Content -LiteralPath $destination -ErrorAction Stop
    if ($existing | Where-Object { $_ -match '^HOLD_PROOF_KEY_BASE64=' }) {
        Write-Host 'HOLD_PROOF_KEY_BASE64 already exists in .env; leaving it unchanged.'
        exit 0
    }
}
$bytes = New-Object 'byte[]' 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$value = [Convert]::ToBase64String($bytes)
if (Test-Path $destination) {
    [System.IO.File]::AppendAllText($destination, "`n$name=$value`n", (New-Object System.Text.UTF8Encoding($false)))
}
else {
    [System.IO.File]::WriteAllText($destination, "$name=$value`n", (New-Object System.Text.UTF8Encoding($false)))
}
Write-Host 'Created local hold proof signing key in git-ignored .env.'
Write-Host 'Do not commit the .env file or share this key.'
