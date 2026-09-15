# Verifies Phase 21: Mini.AuthorizationService gains a policy-admin API and publishes a REAL
# PolicyChangedEvent when a Policy row changes - making Phase 20's consumer + webhook fan-out path
# reachable without Mini.MessageCenter's now-removed throwaway diagnostic endpoint.
#
# This is the first script in the repo to drive the full arc end to end:
#   PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}   (Mini.AuthorizationService, :5015)
#     -> Policy row updated, matching CachedDecisions invalidated, PolicyChangedEvent published
#   -> real RabbitMQ bus (Phase 19)
#   -> Mini.MessageCenter's PolicyChangedEventConsumer (Phase 20, :5017)
#     -> webhook fan-out, HMAC-signed, to WebhookReceiverStub (:5018) and Mini.AcmeApi (:5014)
#
# Needs a REAL RabbitMQ (Phase 19's dependency), same as test-phase19.ps1/test-phase20.ps1. Start
# everything with .\run-all.ps1 first. If Docker isn't available in your environment, this script
# cannot complete past the parts that don't touch the bus - see the Phase 21 README section (in
# src/Mini.AuthorizationService/README.md) for what was verified without it in this environment.

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class TestPhase21CertPolicy {
    public static void TrustAll() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate (object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@ -ErrorAction SilentlyContinue
[TestPhase21CertPolicy]::TrustAll()

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
Write-Host "Phase 21: Mini.AuthorizationService's policy-admin API + real PolicyChangedEvent publish" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. All three services respond to /health" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/health"
if ($resp.StatusCode -ne 200) { throw "Mini.AuthorizationService /health failed: $($resp.StatusCode)" }
$resp = Call "https://localhost:5017/health"
if ($resp.StatusCode -ne 200) { throw "Mini.MessageCenter /health failed: $($resp.StatusCode)" }
$resp = Call "https://localhost:5018/health"
if ($resp.StatusCode -ne 200) { throw "WebhookReceiverStub /health failed: $($resp.StatusCode)" }
Write-Host "  ✓ Mini.AuthorizationService, Mini.MessageCenter, WebhookReceiverStub all healthy" -ForegroundColor Green

Write-Host ""
Write-Host "§2. The admin endpoint requires authentication, but not a specific role (decision #1)" -ForegroundColor Yellow
$anonBody = @{ condition = '{"requiredRoles": ["Admin"]}' } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $null "PUT" $anonBody
if ($resp.StatusCode -ne 401) { throw "Expected 401 for an anonymous PUT, got $($resp.StatusCode)" }
Write-Host "  ✓ anonymous PUT rejected with 401" -ForegroundColor Green

$token = GetClientCredentialsToken "userservice-svc.acme" "acme-userservice-secret" "authapi"
if (-not $token) { throw "Failed to get authapi token for userservice-svc.acme" }
Write-Host "  ✓ a plain authenticated service-account token (no special role) is accepted - proven in §4" -ForegroundColor Green

Write-Host ""
Write-Host "§3. Capture the current 'sample-api' policy for acme before changing it" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/api/v1/authorization/policies" $token
if ($resp.StatusCode -ne 200) { throw "Failed to list policies: $($resp.StatusCode) $($resp.Content)" }
$before = ($resp.Content | ConvertFrom-Json) | Where-Object { $_.resourceName -eq "sample-api" }
if (-not $before) { throw "No 'sample-api' policy found for acme before the update." }
Write-Host "  ✓ acme's 'sample-api' policy exists (Id $($before.id)) before the update" -ForegroundColor Green

Write-Host ""
Write-Host "§4. Prime the decision cache with an Admin-role evaluation (should be authorized, uncached then cached)" -ForegroundColor Yellow
$evalBody = @{ resourceName = "sample-api"; context = @{ role = "Admin" } } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp.StatusCode -ne 200) { throw "Evaluate failed: $($resp.StatusCode) $($resp.Content)" }
$result = $resp.Content | ConvertFrom-Json
if ($result.authorized -ne $true) { throw "Expected authorized=true for Admin before the policy change, got $($result | ConvertTo-Json)" }
if ($result.cached -ne $false) { throw "Expected the first evaluate call to be uncached, got $($result | ConvertTo-Json)" }
$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
$result = $resp.Content | ConvertFrom-Json
if ($result.cached -ne $true) { throw "Expected the second identical evaluate call to be served from cache, got $($result | ConvertTo-Json)" }
Write-Host "  ✓ Admin is authorized for acme/sample-api, and the decision is now cached" -ForegroundColor Green

Write-Host ""
Write-Host "§5. Update the policy through the new admin endpoint - tighten it to require 'SuperAdmin'" -ForegroundColor Yellow
$marker = "phase21-" + [Guid]::NewGuid().ToString("N")
$newCondition = "{`"requiredRoles`": [`"SuperAdmin`"], `"phase21Marker`": `"$marker`"}"
$updateBody = @{ condition = $newCondition } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $token "PUT" $updateBody
if ($resp.StatusCode -ne 200) { throw "Policy update failed: $($resp.StatusCode) $($resp.Content)" }
$updated = $resp.Content | ConvertFrom-Json
if ($updated.condition -ne $newCondition) { throw "Updated policy's Condition doesn't match what was sent." }
Write-Host "  ✓ policy updated (marker: $marker)" -ForegroundColor Green

Write-Host ""
Write-Host "§6. The write is real: GET /policies still lists the resource, and re-evaluating denies Admin now" -ForegroundColor Yellow
$resp = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp.StatusCode -ne 200) { throw "Evaluate failed after update: $($resp.StatusCode) $($resp.Content)" }
$result = $resp.Content | ConvertFrom-Json
if ($result.cached -ne $false) { throw "Expected the post-update evaluate call to MISS the cache (invalidated by the update), got $($result | ConvertTo-Json)" }
if ($result.authorized -ne $false) { throw "Expected authorized=false for Admin after tightening to SuperAdmin, got $($result | ConvertTo-Json)" }
Write-Host "  ✓ cache invalidation worked: no 30-second wait needed, the very next call re-evaluated and denied Admin" -ForegroundColor Green

Write-Host ""
Write-Host "§7. Mini.MessageCenter's real consumer received the PolicyChangedEvent off RabbitMQ" -ForegroundColor Yellow
$deadline = (Get-Date).AddSeconds(20)
$sawMarker = $false
while ((Get-Date) -lt $deadline -and -not $sawMarker) {
    Start-Sleep -Milliseconds 500
    $resp = Call "https://localhost:5018/webhook/received"
    if ($resp.StatusCode -ne 200) { continue }
    $entries = $resp.Content | ConvertFrom-Json
    foreach ($e in $entries) {
        if ($e.body -like "*$marker*") { $sawMarker = $true }
    }
}
if (-not $sawMarker) { throw "WebhookReceiverStub never received a delivery containing marker '$marker' - the real publish -> bus -> consumer -> webhook path did not complete." }
Write-Host "  ✓ WebhookReceiverStub received a REAL delivery (not the removed diagnostic endpoint) carrying the new Condition" -ForegroundColor Green

Write-Host ""
Write-Host "§8. Mini.MessageCenter's delivery history records this fan-out too" -ForegroundColor Yellow
$resp = Call "https://localhost:5017/api/v1/deliveries"
if ($resp.StatusCode -ne 200) { throw "Failed to read delivery history: $($resp.StatusCode)" }
$deliveries = $resp.Content | ConvertFrom-Json
$matching = $deliveries | Where-Object { $_.eventSummary -like "*$marker*" }
if ($matching.Count -lt 2) { throw "Expected at least 2 delivery attempts (acme-scoped + unscoped) for marker '$marker', got $($matching.Count)." }
Write-Host "  ✓ delivery history shows $($matching.Count) attempt(s) for this real policy change" -ForegroundColor Green

Write-Host ""
Write-Host "§9. Restore acme's original 'sample-api' policy so the script is repeatable" -ForegroundColor Yellow
$restoreBody = @{ condition = $before.condition } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $token "PUT" $restoreBody
if ($resp.StatusCode -ne 200) { throw "Failed to restore original policy: $($resp.StatusCode) $($resp.Content)" }
Write-Host "  ✓ restored to: $($before.condition)" -ForegroundColor Green

Write-Host ""
Write-Host "Phase 21 verified." -ForegroundColor Green
Write-Host "  - PUT /api/v1/authorization/policies/{tenantKey}/{resourceName} requires auth, not a role" -ForegroundColor DarkGray
Write-Host "  - a successful update invalidates matching CachedDecisions immediately (no 30s wait)" -ForegroundColor DarkGray
Write-Host "  - a successful update publishes a REAL PolicyChangedEvent onto the bus" -ForegroundColor DarkGray
Write-Host "  - Mini.MessageCenter's Phase 20 consumer + webhook fan-out now has a genuine trigger" -ForegroundColor DarkGray
