# Run from the TicketRush repository root after applying the migration patch.
# Backs up the latest test file and replaces only the old schema initialization.
$ErrorActionPreference = 'Stop'
$path = Join-Path (Get-Location) 'tests/TicketRush.IntegrationTests/BookingConcurrencyTests.cs'
if (-not (Test-Path $path)) { throw "Test file not found: $path (run from solution root)." }
$content = [System.IO.File]::ReadAllText($path)

$braced = '(?s)await\s+using\s*\(\s*var\s+setup\s*=\s*new\s+BookingDbContext\s*\(\s*options\s*\)\s*\)\s*\{\s*var\s+schema\s*=\s*await\s+File\.ReadAllTextAsync\s*\(\s*Path\.Combine\s*\(\s*AppContext\.BaseDirectory\s*,\s*"001_schema\.sql"\s*\)\s*\)\s*;\s*await\s+setup\.Database\.ExecuteSqlRawAsync\s*\(\s*schema\s*\)\s*;\s*\}'
$unbraced = '(?s)await\s+using\s*\(\s*var\s+setup\s*=\s*new\s+BookingDbContext\s*\(\s*options\s*\)\s*\)\s*await\s+setup\.Database\.ExecuteSqlRawAsync\s*\(\s*await\s+File\.ReadAllTextAsync\s*\(\s*Path\.Combine\s*\(\s*AppContext\.BaseDirectory\s*,\s*"001_schema\.sql"\s*\)\s*\)\s*\)\s*;'
$replacement = 'await TestDatabaseInitializer.InitializeAsync(options);'
$converted = [regex]::Replace($content, $braced, $replacement)
$converted = [regex]::Replace($converted, $unbraced, $replacement)

if ($converted -eq $content) {
    if ($content -match '001_schema\.sql') {
        throw 'Found handwritten-schema references but no recognized initializer pattern. Convert manually; file was not modified.'
    }
    Write-Host 'Nothing to convert; tests already appear migrated.'
    exit 0
}
if ($converted -match '001_schema\.sql') {
    throw 'Unrecognized handwritten-schema references remain. No changes written; review tests manually.'
}
$backup = "$path.pre-migrations.bak"
if (Test-Path $backup) { throw "Backup already exists: $backup. Move it before rerunning." }
[System.IO.File]::WriteAllText($backup, $content)
[System.IO.File]::WriteAllText($path, $converted)
Write-Host "Converted test schema initialization; original backed up at $backup"
