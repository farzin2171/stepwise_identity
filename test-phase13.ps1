# Verifies Phase 13: authorization decisions made by a separate service, not embedded in the token.
#
# Start everything with .\run-all.ps1 first. Mini.AuthorizationService (:5015) is new in Phase 13
# and is in the default set. The service does NOT yet intercept any login path - tokens still carry
# the `role` claim - but the endpoint exists and can be called manually.
#
# The regression suite for this phase is every earlier test-phase*.ps1, run unmodified. They all still pass
# because phase 13 is additive: the authorization service exists and is callable, but no caller has changed
# yet to use it.

$ErrorActionPreference = "Stop"

function Base64UrlEncode([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function NewClient() {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    return New-Object System.Net.Http.HttpClient($handler)
}

function Call($uri, $token, $method = "GET", $json = $null) {
    $client = NewClient
    $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
    if ($token) {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
    }
    if ($json) {
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    return @{
        StatusCode = [int]$response.StatusCode
        Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function GetClientCredentialsToken($clientId, $secret, $scope) {
    $body = @{ grant_type = "client_credentials"; client_id = $clientId; client_secret = $secret }
    if ($scope) { $body.scope = $scope }

    $client = NewClient
    $request = [System.Net.Http.HttpRequestMessage]::new("POST", "https://localhost:5001/connect/token")
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($k in $body.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, $body[$k])) }
    $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    $response = $client.SendAsync($request).GetAwaiter().GetResult()

    if ($response.StatusCode -ne 200) { throw "Client-credentials grant failed for '$clientId': $($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())" }
    return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

Write-Host ""
Write-Host "Phase 13: Mini.AuthorizationService (authorization decisions as a service)" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. Authorization service responds to /health" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/health"
if ($resp.StatusCode -ne 200) { throw "Authorization service /health failed: $($resp.StatusCode)" }
Write-Host "  ✓ /health returned 200" -ForegroundColor Green

Write-Host ""
Write-Host "§2. Authorization service requires a token for /api/v1/authorization endpoints" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/api/v1/authorization/policies"
if ($resp.StatusCode -ne 401) { throw "Expected 401 for unauthenticated request, got $($resp.StatusCode)" }
Write-Host "  ✓ unauthenticated request to /api/v1/authorization/policies returned 401" -ForegroundColor Green

Write-Host ""
Write-Host "§3. Service accounts can get an authapi token from IdentityServerHost" -ForegroundColor Yellow
$token = GetClientCredentialsToken "userservice-svc.acme" "acme-userservice-secret" "authapi"
if (-not $token) { throw "Failed to get authapi token" }
Write-Host "  ✓ userservice-svc.acme received a token for authapi scope" -ForegroundColor Green

Write-Host ""
Write-Host "§4. Authorization service can list policies for a tenant" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/api/v1/authorization/policies" $token
if ($resp.StatusCode -ne 200) { throw "Failed to list policies: $($resp.StatusCode) $($resp.Content)" }
$policies = $resp.Content | ConvertFrom-Json
if ($policies.Count -eq 0) { throw "No policies found for acme" }
Write-Host "  ✓ listed $($policies.Count) policies for acme tenant" -ForegroundColor Green

Write-Host ""
Write-Host "§5. Authorization service evaluates policies (allow case)" -ForegroundColor Yellow
$evalBody = @{
    resourceName = "sample-api"
    context = @{ role = "Admin" }
} | ConvertTo-Json

$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp.StatusCode -ne 200) { throw "Evaluation failed: $($resp.StatusCode) $($resp.Content)" }
$result = $resp.Content | ConvertFrom-Json
if ($result.authorized -ne $true) { throw "Expected authorized=true for Admin role, got $($result | ConvertTo-Json)" }
Write-Host "  ✓ Admin on sample-api granted access: '$($result.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "§6. Authorization service evaluates policies (deny case)" -ForegroundColor Yellow
$evalBody = @{
    resourceName = "sample-api"
    context = @{ role = "Member" }
} | ConvertTo-Json

$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp.StatusCode -ne 200) { throw "Evaluation failed: $($resp.StatusCode) $($resp.Content)" }
$result = $resp.Content | ConvertFrom-Json
if ($result.authorized -ne $false) { throw "Expected authorized=false for Member role on sample-api, got $($result | ConvertTo-Json)" }
Write-Host "  ✓ Member role denied access (acme only allows Admin): '$($result.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "§7. Globex tenant has different policy" -ForegroundColor Yellow
$tokenGlobex = GetClientCredentialsToken "userservice-svc.globex" "globex-userservice-secret" "authapi"
$evalBody = @{
    resourceName = "sample-api"
    context = @{ role = "Member" }
} | ConvertTo-Json

$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $tokenGlobex "POST" $evalBody
if ($resp.StatusCode -ne 200) { throw "Evaluation failed: $($resp.StatusCode) $($resp.Content)" }
$result = $resp.Content | ConvertFrom-Json
if ($result.authorized -ne $true) { throw "Expected authorized=true for Globex Member, got $($result | ConvertTo-Json)" }
Write-Host "  ✓ Globex Member on sample-api granted access (tenant-specific policy): '$($result.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "✓ Phase 13 verification complete" -ForegroundColor Green
