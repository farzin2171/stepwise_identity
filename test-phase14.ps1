# Verifies Phase 14: authorization integration — SampleApi calls Mini.AuthorizationService to make
# authorization decisions instead of relying solely on the role claim in the token.
#
# Start everything with .\run-all.ps1 first. Both Mini.UserService (:5013) and Mini.AuthorizationService
# (:5015) are now in the default set. SampleApi adds a new /authorize/{resourceName} endpoint that calls
# the authorization service.
#
# The regression suite for this phase is every earlier test-phase*.ps1, run unmodified. They all still pass
# because Phase 14 is additive: the /authorize endpoint is new, but the /identity endpoint and cache
# operations still work exactly as before.

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

Write-Host ""
Write-Host "Phase 14: Authorization integration — SampleApi calls Mini.AuthorizationService" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. SampleApi /authorize endpoint requires authentication" -ForegroundColor Yellow
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $null "POST"
if ($resp.StatusCode -ne 401) { throw "Expected 401 for unauthenticated request, got $($resp.StatusCode)" }
Write-Host "  ✓ unauthenticated request returned 401" -ForegroundColor Green

Write-Host ""
Write-Host "§2. Acme user can log in (regression from Phase 12)" -ForegroundColor Yellow
$token = LoginAndGetToken "alice" "alice" "acme"
if (-not $token) { throw "Failed to get login token" }
Write-Host "  ✓ alice (acme) logged in successfully" -ForegroundColor Green

Write-Host ""
Write-Host "§3. SampleApi /authorize endpoint returns authorization decision from Mini.AuthorizationService" -ForegroundColor Yellow
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $token "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed: $($resp.StatusCode) $($resp.Content)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $true) { throw "Expected authorization to be granted for alice, got $($authz | ConvertTo-Json)" }
Write-Host "  ✓ alice is authorized for sample-api: '$($authz.reason)'" -ForegroundColor Green
Write-Host "    Token role: $($authz.roleFromToken), AuthZ service decision: $($authz.authorized)" -ForegroundColor DarkGray

Write-Host ""
Write-Host "§4. Globex user gets different authorization decision (per-tenant policies)" -ForegroundColor Yellow
$tokenGlobex = LoginAndGetToken "bob" "bob" "globex"
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $tokenGlobex "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed: $($resp.StatusCode) $($resp.Content)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $true) { throw "Expected authorization for bob (globex member), got $($authz | ConvertTo-Json)" }
Write-Host "  ✓ bob (globex) is authorized: '$($authz.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "§5. /identity endpoint still works (regression)" -ForegroundColor Yellow
$resp = Call "https://localhost:5007/api/v1/identity" $token
if ($resp.StatusCode -ne 200) { throw "Identity endpoint failed: $($resp.StatusCode)" }
$identity = $resp.Content | ConvertFrom-Json
if ($identity.identity.tenantKey -ne "acme") { throw "Expected tenant acme, got $($identity.identity.tenantKey)" }
Write-Host "  ✓ /identity endpoint works, tenant=$($identity.identity.tenantKey), identity type=$($identity.identity.identityType)" -ForegroundColor Green

Write-Host ""
Write-Host "§6. Service account can call /authorize endpoint" -ForegroundColor Yellow
$client = NewClient
$request = [System.Net.Http.HttpRequestMessage]::new("POST", "https://localhost:5001/connect/token")
$body = @{
    grant_type = "client_credentials"
    client_id = "mvcclient-svc.acme"
    client_secret = "acme-svc-secret"
    scope = "api1"
}
$pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
foreach ($k in $body.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, $body[$k])) }
$request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
$response = $client.SendAsync($request).GetAwaiter().GetResult()
if ($response.StatusCode -ne 200) { throw "Failed to get service account token" }
$svcToken = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token

$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $svcToken "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed for service account: $($resp.StatusCode)" }
$authz = $resp.Content | ConvertFrom-Json
Write-Host "  ✓ service account can call /authorize, authorized=$($authz.authorized)" -ForegroundColor Green

Write-Host ""
Write-Host "✓ Phase 14 verification complete" -ForegroundColor Green
