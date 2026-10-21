# DEVELOPMENT/SMOKE TEST ONLY. Password grant is NOT a production authentication flow.
# Uses local-only demo accounts imported into the Keycloak container.
param(
    [ValidateSet('alice','bob')][string]$Username = 'alice',
    [string]$KeycloakUrl = 'http://localhost:8092'
)
$ErrorActionPreference = 'Stop'
$password = if ($Username -eq 'alice') { 'LocalOnly#Alice42' } else { 'LocalOnly#Bob42' }
$tokenEndpoint = $KeycloakUrl.TrimEnd('/') + '/realms/ticketrush/protocol/openid-connect/token'
$response = $null
$lastError = $null
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
        $response = Invoke-RestMethod -Method Post -Uri $tokenEndpoint `
            -ContentType 'application/x-www-form-urlencoded' -Body @{
                client_id = 'ticketrush-smoke'
                grant_type = 'password'
                username = $Username
                password = $password
            } -ErrorAction Stop
        break
    }
    catch {
        $lastError = $_.Exception.Message
        Start-Sleep -Seconds 2
    }
}
if ($null -eq $response) {
    throw "Local Keycloak token request failed: $lastError"
}
if ([string]::IsNullOrWhiteSpace([string]$response.access_token)) {
    throw 'The local Keycloak server did not issue an access token.'
}
# Fail early if the imported realm is issuing tokens for an incorrect audience,
# issuer or demo identity. These are diagnostic checks, NOT signature validation;
# both APIs perform the actual JWT signature and lifetime validation.
$token = [string]$response.access_token
$parts = $token.Split('.')
if ($parts.Length -ne 3) {
    throw 'Keycloak returned an incorrectly formatted JWT access token.'
}
$encodedPayload = $parts[1].Replace('-', '+').Replace('_', '/')
switch ($encodedPayload.Length % 4) {
    0 { }
    2 { $encodedPayload += '==' }
    3 { $encodedPayload += '=' }
    default { throw 'Keycloak JWT payload has invalid base64url padding.' }
}
try {
    $payloadJson = [System.Text.Encoding]::UTF8.GetString(
        [Convert]::FromBase64String($encodedPayload))
    $claims = $payloadJson | ConvertFrom-Json
}
catch {
    throw "Could not decode the Keycloak JWT payload: $($_.Exception.Message)"
}
$expectedIssuer = 'http://keycloak:8080/realms/ticketrush'
$expectedBuyer = if ($Username -eq 'alice') {
    'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
} else {
    'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
}
if ([string]$claims.iss -ne $expectedIssuer) {
    throw "Unexpected JWT issuer '$($claims.iss)'; expected '$expectedIssuer'."
}
if (@($claims.aud) -notcontains 'ticketrush-apis') {
    throw 'Keycloak JWT lacks the ticketrush-apis audience. Check the realm audience mapper and recreate the Keycloak container.'
}
if ([string]$claims.sub -ne $expectedBuyer) {
    throw "Unexpected JWT buyer subject '$($claims.sub)'; expected '$expectedBuyer'."
}
Write-Output $token
