# Verifies Phase 15: Mini.AuthorizationService's decision cache. POST /evaluate now caches its
# decision (CachedDecisions table); a repeat call for the same caller/resource/context returns the
# cached decision instead of re-evaluating policies. Unlike an in-process Dictionary, the cache is a
# SQL Server table — it survives a restart of Mini.AuthorizationService (not exercised here, since
# restarting a process mid-script needs run-all.ps1's own process management; see the "Try it
# yourself" section in src/Mini.AuthorizationService/README.md for a manual way to see this).
#
# Start everything with .\run-all.ps1 first.

$ErrorActionPreference = "Stop"

function Base64UrlEncode([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function NewPkcePair() {
    $verifierBytes = [byte[]]::new(32)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($verifierBytes)
    $verifier = Base64UrlEncode $verifierBytes
    $challenge = Base64UrlEncode ([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::ASCII.GetBytes($verifier)))
    return @{ Verifier = $verifier; Challenge = $challenge }
}

function NewClient() {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    return New-Object System.Net.Http.HttpClient($handler)
}

function Follow($client, $uri, $method = "GET", $formFields = $null, $stopAtHost = $null) {
    for ($i = 0; $i -lt 15; $i++) {
        $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
        if ($formFields) {
            $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
            foreach ($k in $formFields.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, $formFields[$k])) }
            $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($status -lt 300 -or $status -ge 400) { return @{ StatusCode = $status; Content = $content; Uri = $uri } }
        $location = $response.Headers.Location
        $uri = if ($location.IsAbsoluteUri) { $location.ToString() } else { [System.Uri]::new([System.Uri]$uri, $location).ToString() }
        if ($stopAtHost -and $uri -like "*$stopAtHost*") { return @{ StatusCode = $status; Content = $content; Uri = $uri } }
        $method = "GET"; $formFields = $null
    }
    throw "Too many redirects, stopped at $uri"
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

function LoginAndGetToken($username, $password, $tenantKey) {
    $client = NewClient
    $pkce = NewPkcePair
    $authorizeUrl = "https://localhost:5001/connect/authorize?client_id=reactspa&redirect_uri=" + `
        [uri]::EscapeDataString("http://localhost:5173/callback") + `
        "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile api1 tenant") + `
        "&code_challenge=$($pkce.Challenge)&code_challenge_method=S256&state=teststate123" + `
        "&acr_values=" + [uri]::EscapeDataString("tenant:$tenantKey")

    $resp = Follow $client $authorizeUrl
    $verToken = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
    $returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
    $body = @{ Username = $username; Password = $password; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
    $resp = Follow $client "https://localhost:5001/Account/Login" "POST" $body "localhost:5173"

    $code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]
    $resp = Follow $client "https://localhost:5001/connect/token" "POST" @{
        grant_type = "authorization_code"; code = $code
        redirect_uri = "http://localhost:5173/callback"; client_id = "reactspa"; code_verifier = $pkce.Verifier
    }
    if ($resp.StatusCode -ne 200) { throw "Token endpoint failed: $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function GetServiceAccountToken($clientId, $clientSecret) {
    $client = NewClient
    $request = [System.Net.Http.HttpRequestMessage]::new("POST", "https://localhost:5001/connect/token")
    $body = @{ grant_type = "client_credentials"; client_id = $clientId; client_secret = $clientSecret; scope = "api1" }
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($k in $body.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, $body[$k])) }
    $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if ($response.StatusCode -ne 200) { throw "Failed to get service account token for $clientId" }
    return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

Write-Host ""
Write-Host "Phase 15: Mini.AuthorizationService's persisted decision cache" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. Alice (acme) logs in (regression from Phase 12)" -ForegroundColor Yellow
$token = LoginAndGetToken "alice" "alice" "acme"
if (-not $token) { throw "Failed to get login token" }
Write-Host "  ✓ alice (acme) logged in successfully" -ForegroundColor Green

Write-Host ""
Write-Host "§2. First evaluate call misses the cache, second identical call is served from it" -ForegroundColor Yellow
$evalBody = '{"resourceName": "sample-api", "context": {"role": "Admin", "caller": "User"}}'
$resp1 = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp1.StatusCode -ne 200) { throw "Evaluate call failed: $($resp1.StatusCode) $($resp1.Content)" }
$decision1 = $resp1.Content | ConvertFrom-Json
if ($decision1.cached -ne $false) { throw "Expected first call to be a cache miss, got $($decision1 | ConvertTo-Json)" }
Write-Host "  ✓ first evaluate call is a cache miss (cached=$($decision1.cached))" -ForegroundColor Green

$resp2 = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp2.StatusCode -ne 200) { throw "Evaluate call failed: $($resp2.StatusCode) $($resp2.Content)" }
$decision2 = $resp2.Content | ConvertFrom-Json
if ($decision2.cached -ne $true) { throw "Expected second identical call to be a cache hit, got $($decision2 | ConvertTo-Json)" }
if ($decision2.authorized -ne $decision1.authorized -or $decision2.reason -ne $decision1.reason) {
    throw "Cached decision doesn't match the original: $($decision1 | ConvertTo-Json) vs $($decision2 | ConvertTo-Json)"
}
Write-Host "  ✓ second identical call is a cache hit (cached=$($decision2.cached)), same decision" -ForegroundColor Green

Write-Host ""
Write-Host "§3. A different context is a different cache entry (not a false cache hit)" -ForegroundColor Yellow
$memberBody = '{"resourceName": "sample-api", "context": {"role": "Member", "caller": "User"}}'
$resp3 = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $memberBody
if ($resp3.StatusCode -ne 200) { throw "Evaluate call failed: $($resp3.StatusCode) $($resp3.Content)" }
$decision3 = $resp3.Content | ConvertFrom-Json
if ($decision3.cached -ne $false) { throw "Expected a different context to miss the cache, got $($decision3 | ConvertTo-Json)" }
Write-Host "  ✓ different context ('Member' vs 'Admin') is its own cache miss (cached=$($decision3.cached))" -ForegroundColor Green

Write-Host ""
Write-Host "§4. SampleApi's /authorize endpoint benefits from the same cache (Phase 14 integration, still working)" -ForegroundColor Yellow
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $token "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed: $($resp.StatusCode) $($resp.Content)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $true) { throw "Expected alice to be authorized, got $($authz | ConvertTo-Json)" }
Write-Host "  ✓ alice authorized via SampleApi: '$($authz.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "§5. Service account clears the tenant's cache via SampleApi's admin/cache endpoint" -ForegroundColor Yellow
# mvcclient-svc.acme, not userservice-svc.acme: SampleApi validates tokens against the "api1"
# audience, and only the mvcclient-svc.* clients are allowed that scope (userservice-svc.* clients
# are scoped to "acmeapi" instead — see IdentityServerConfig.json).
$svcToken = GetServiceAccountToken "mvcclient-svc.acme" "acme-svc-secret"
$resp = Call "https://localhost:5007/api/v1/admin/cache/acme" $svcToken "DELETE"
if ($resp.StatusCode -ne 200) { throw "Cache clear failed: $($resp.StatusCode) $($resp.Content)" }
$clearResult = $resp.Content | ConvertFrom-Json
Write-Host "  ✓ cache cleared: $($clearResult.message)" -ForegroundColor Green

Write-Host ""
Write-Host "§6. After clearing, the next identical call misses the cache again" -ForegroundColor Yellow
$resp4 = Call "https://localhost:5015/api/v1/authorization/evaluate" $token "POST" $evalBody
if ($resp4.StatusCode -ne 200) { throw "Evaluate call failed: $($resp4.StatusCode) $($resp4.Content)" }
$decision4 = $resp4.Content | ConvertFrom-Json
if ($decision4.cached -ne $false) { throw "Expected a cache miss after clearing, got $($decision4 | ConvertTo-Json)" }
Write-Host "  ✓ post-clear call is a cache miss (cached=$($decision4.cached))" -ForegroundColor Green

Write-Host ""
Write-Host "§7. A non-service-account cannot clear the cache (regression from Phase 14's admin/cache gating)" -ForegroundColor Yellow
$resp = Call "https://localhost:5007/api/v1/admin/cache/acme" $token "DELETE"
if ($resp.StatusCode -ne 403) { throw "Expected 403 for a user token calling admin/cache, got $($resp.StatusCode)" }
Write-Host "  ✓ alice's user token was refused with 403" -ForegroundColor Green

Write-Host ""
Write-Host "✓ Phase 15 verification complete" -ForegroundColor Green
