# Verifies Phase 16: SampleApi's IAuthorizationClient/AuthorizationClient moved into
# Mini.Infrastructure/ExternalServices, config-driven (ExternalServicesApi:ServiceDefinitions:AuthorizationService)
# instead of a hardcoded https://localhost:5015, and wired with the same AddHttpClient<T>() +
# ResiliencePolicies.Retry()/CircuitBreaker() convention every other named client in this repo uses —
# see src/SampleApi/Program.cs and src/Mini.Infrastructure/ExternalServices/AuthorizationClient.cs.
#
# The regression suite for this phase is every earlier test-phase*.ps1 (13, 14, 15 especially — they
# exercise the exact same /authorize and /evaluate call path this phase only re-wired, not redesigned),
# run unmodified. This script adds the one thing those can't show: that a downed Mini.AuthorizationService
# is now actually retried with backoff before SampleApi gives up, where before Phase 16 the very first
# connection failure was caught and returned immediately — no retry, no circuit breaker, despite every
# other cross-service HTTP call in this repo having both since Phase 10.
#
# Requires .\run-all.ps1 to already be running (this script stops and restarts ONLY
# Mini.AuthorizationService, using the pid recorded in .run-all.pids).

$ErrorActionPreference = "Stop"

function NewClient() {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    return New-Object System.Net.Http.HttpClient($handler)
}

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
Write-Host "Phase 16: Shared authorization client — extracted into Mini.Infrastructure, now resilient" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. SampleApi /authorize still works against the config-driven, typed client (regression)" -ForegroundColor Yellow
$token = LoginAndGetToken "alice" "alice" "acme"
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $token "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed: $($resp.StatusCode) $($resp.Content)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $true) { throw "Expected authorization to be granted for alice, got $($authz | ConvertTo-Json)" }
Write-Host "  ✓ alice is authorized for sample-api: '$($authz.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "§2. Stopping Mini.AuthorizationService to observe the (now-present) retry behavior" -ForegroundColor Yellow
$pidFile = Join-Path $PSScriptRoot ".run-all.pids"
if (-not (Test-Path $pidFile)) { throw "$pidFile not found — start everything with .\run-all.ps1 first." }
$lines = Get-Content $pidFile
$authzLine = $lines | Where-Object { $_ -match "^\d+,Mini\.AuthorizationService$" }
if (-not $authzLine) { throw "Mini.AuthorizationService isn't in $pidFile — is run-all.ps1 running with the default service set?" }
$authzProcessId = [int]($authzLine -split ",")[0]
Stop-Process -Id $authzProcessId -Force
Write-Host "  stopped Mini.AuthorizationService (pid $authzProcessId)" -ForegroundColor Green

Write-Host ""
Write-Host "§3. /authorize against a downed Mini.AuthorizationService now takes visibly longer, not less" -ForegroundColor Yellow
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $token "POST"
$sw.Stop()
if ($resp.StatusCode -ne 200) { throw "Expected SampleApi to still answer 200 (with authorized=false), got $($resp.StatusCode)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $false) { throw "Expected authorized=false with the authorization service down, got $($authz | ConvertTo-Json)" }
# ResiliencePolicies.Retry() waits 2s then 4s between the 3 attempts it makes (see
# Mini.Infrastructure/Http/ResiliencePolicies.cs) — at least 6 seconds is the signature that retries
# actually ran, versus the pre-Phase-16 behavior of failing on the very first connection attempt.
if ($sw.Elapsed.TotalSeconds -lt 5.5) {
    throw "Expected the call to take at least ~6s (2s + 4s retry backoff), took only $($sw.Elapsed.TotalSeconds)s — retries may not be wired up."
}
Write-Host "  ✓ call failed gracefully (authorized=false, reason: '$($authz.reason)') after $([math]::Round($sw.Elapsed.TotalSeconds, 1))s of retries" -ForegroundColor Green

Write-Host ""
Write-Host "§4. Restarting Mini.AuthorizationService and confirming recovery" -ForegroundColor Yellow
$process = Start-Process -FilePath "dotnet" `
    -ArgumentList @("run", "--no-build", "--urls", "https://localhost:5015") `
    -WorkingDirectory (Join-Path $PSScriptRoot "src/Mini.AuthorizationService") `
    -PassThru -WindowStyle Hidden
$newLines = ($lines | Where-Object { $_ -ne $authzLine }) + "$($process.Id),Mini.AuthorizationService"
Set-Content -Path $pidFile -Value $newLines

Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class Phase16CertPolicy {
    public static void TrustAll() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate (object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@ -ErrorAction SilentlyContinue
[Phase16CertPolicy]::TrustAll()

$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        $health = Invoke-WebRequest -Uri "https://localhost:5015/health" -UseBasicParsing -TimeoutSec 2
        if ($health.StatusCode -eq 200) { $healthy = $true; break }
    } catch {}
    Start-Sleep -Seconds 1
}
if (-not $healthy) { throw "Mini.AuthorizationService didn't come back healthy within 30s" }
Write-Host "  ✓ Mini.AuthorizationService (pid $($process.Id)) is healthy again" -ForegroundColor Green

# §3's single call made 3 failed attempts (the original try plus 2 retries) against SampleApi's
# CircuitBreaker() policy, which trips open after 3 consecutive failures for 30 seconds — the exact
# gotcha Phase 9's README already names for ExternalServicesStub: a dependency that's back up still
# looks broken from inside that window ("The circuit is now open..." rather than a real failure). Wait
# it out rather than pretend the breaker doesn't apply here too.
Write-Host "  (waiting out CircuitBreaker()'s 30s open window before the next call — same gotcha Phase 9 hit)" -ForegroundColor DarkGray
Start-Sleep -Seconds 31

$resp = Call "https://localhost:5007/api/v1/authorize/sample-api" $token "POST"
if ($resp.StatusCode -ne 200) { throw "Authorization check failed after recovery: $($resp.StatusCode) $($resp.Content)" }
$authz = $resp.Content | ConvertFrom-Json
if ($authz.authorized -ne $true) { throw "Expected authorization to be granted again for alice, got $($authz | ConvertTo-Json)" }
Write-Host "  ✓ alice is authorized for sample-api again: '$($authz.reason)'" -ForegroundColor Green

Write-Host ""
Write-Host "✓ Phase 16 verification complete" -ForegroundColor Green
