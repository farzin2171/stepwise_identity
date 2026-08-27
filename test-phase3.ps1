# Verifies tenant resolution end-to-end against the "reactspa" public client (simplest to script - no
# secret needed). Covers: matching tenant succeeds with a tenant_id claim, mismatched tenant is rejected,
# and no acr_values at all still works (Phase 2 behavior unaffected).
# Run IdentityServerHost first (dotnet run), then run this script.

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

function Follow($client, $uri, $method = "GET", $formFields = $null, $stopAtHost = $null) {
    for ($i = 0; $i -lt 10; $i++) {
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

function TryLogin($username, $password, $tenantHint) {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    $client = New-Object System.Net.Http.HttpClient($handler)

    $pkce = NewPkcePair
    $scope = if ($tenantHint) { "openid profile tenant" } else { "openid profile" }
    $authorizeUrl = "http://localhost:5000/connect/authorize?client_id=reactspa&redirect_uri=" + `
        [uri]::EscapeDataString("http://localhost:5173/callback") + `
        "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString($scope) + `
        "&code_challenge=$($pkce.Challenge)&code_challenge_method=S256&state=teststate123"
    if ($tenantHint) { $authorizeUrl += "&acr_values=" + [uri]::EscapeDataString("tenant:$tenantHint") }

    $resp = Follow $client $authorizeUrl
    if ($resp.Content -notmatch "Sign in") { throw "Expected the login page, got: $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))" }

    $returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
    $verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
    $body = @{ Username = $username; Password = $password; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
    $resp = Follow $client "http://localhost:5000/Account/Login" "POST" $body "localhost:5173"

    if ($resp.Uri -notmatch "code=") {
        # Rejected - the ModelState error is rendered back into the same login page.
        $errorMatch = [regex]::Match($resp.Content, '<p style="color: #991b1b;">([^<]*)</p>')
        return @{ Success = $false; Error = $errorMatch.Groups[1].Value }
    }

    $code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]
    $tokenBody = @{
        grant_type = "authorization_code"; code = $code
        redirect_uri = "http://localhost:5173/callback"; client_id = "reactspa"; code_verifier = $pkce.Verifier
    }
    $resp = Follow $client "http://localhost:5000/connect/token" "POST" $tokenBody
    if ($resp.StatusCode -ne 200) { throw "Token endpoint failed unexpectedly: $($resp.Content)" }

    # Same fact as the MVC lesson: the code flow's ID token carries only sub by default. oidc-client-ts
    # (what the real React app uses) defaults loadUserInfo=true and calls this endpoint automatically -
    # this script does the same by hand to see the same claims a real browser session would end up with.
    $accessToken = ($resp.Content | ConvertFrom-Json).access_token
    $userinfoRequest = [System.Net.Http.HttpRequestMessage]::new("GET", "http://localhost:5000/connect/userinfo")
    $userinfoRequest.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $accessToken)
    $userinfoResp = $client.SendAsync($userinfoRequest).GetAwaiter().GetResult()
    $claims = ($userinfoResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json
    return @{ Success = $true; TenantId = $claims.tenant_id }
}

Write-Host "1. alice + tenant:acme (matching tenant) should succeed with tenant_id=acme..."
$r = TryLogin "alice" "alice" "acme"
if (-not $r.Success) { throw "Expected success, got rejected: $($r.Error)" }
if ($r.TenantId -ne "acme") { throw "Expected tenant_id claim 'acme', got: $($r.TenantId)" }
Write-Host "   PASS - tenant_id=$($r.TenantId)" -ForegroundColor Green

Write-Host "2. alice + tenant:globex (mismatched tenant) should be rejected..."
$r = TryLogin "alice" "alice" "globex"
if ($r.Success) { throw "Expected rejection, but login succeeded" }
Write-Host "   PASS - rejected with: `"$($r.Error)`"" -ForegroundColor Green

Write-Host "3. bob + tenant:globex (matching tenant) should succeed with tenant_id=globex..."
$r = TryLogin "bob" "bob" "globex"
if (-not $r.Success) { throw "Expected success, got rejected: $($r.Error)" }
if ($r.TenantId -ne "globex") { throw "Expected tenant_id claim 'globex', got: $($r.TenantId)" }
Write-Host "   PASS - tenant_id=$($r.TenantId)" -ForegroundColor Green

Write-Host "4. alice, no acr_values at all, should still succeed (Phase 2 behavior unaffected)..."
$r = TryLogin "alice" "alice" $null
if (-not $r.Success) { throw "Expected success with no tenant hint, got rejected: $($r.Error)" }
Write-Host "   PASS - login without a tenant hint still works" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 3 TENANT RESOLUTION: PASS" -ForegroundColor Green
