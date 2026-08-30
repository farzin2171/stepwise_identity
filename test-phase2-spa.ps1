# Verifies the "reactspa" PUBLIC client end-to-end against IdentityServerHost: authorize -> login ->
# token exchange with NO client secret -> CORS preflight. This is the server-side half of what the React
# app's oidc-client-ts library does in the browser - it proves the IdentityServer config is correct even
# without driving real browser JavaScript (see the React SPA README for what that leaves unverified).

$ErrorActionPreference = "Stop"

function Base64UrlEncode([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$verifierBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($verifierBytes)
$codeVerifier = Base64UrlEncode $verifierBytes
$challengeBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::ASCII.GetBytes($codeVerifier))
$codeChallenge = Base64UrlEncode $challengeBytes

$cookies = New-Object System.Net.CookieContainer
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = $cookies
$client = New-Object System.Net.Http.HttpClient($handler)

function Follow($uri, $method = "GET", $formFields = $null, $stopAtHost = $null) {
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

Write-Host "1. GET /connect/authorize for the public client (no client secret anywhere)..."
$authorizeUrl = "https://localhost:5001/connect/authorize?client_id=reactspa&redirect_uri=" + `
    [uri]::EscapeDataString("http://localhost:5173/callback") + `
    "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile") + `
    "&code_challenge=$codeChallenge&code_challenge_method=S256&state=teststate123"
$resp = Follow $authorizeUrl
if ($resp.Content -notmatch "Sign in") { throw "Expected the login page: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Landed on the login page." -ForegroundColor Green

$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
if (-not $returnUrl -or -not $verToken) { throw "Could not parse ReturnUrl or antiforgery token from login page" }

Write-Host "2. Log in as alice, stop at the redirect back to localhost:5173 (never call it - nothing's serving that route in this script)..."
$body = @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
$resp = Follow "https://localhost:5001/Account/Login" "POST" $body "localhost:5173"
if ($resp.Uri -notmatch "code=") { throw "Expected the final redirect to carry ?code=... (response_mode=query), landed on: $($resp.Uri)" }
$code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]
Write-Host "   Got an authorization code: $($code.Substring(0, 12))..." -ForegroundColor Green

Write-Host "3. POST to /connect/token with the code_verifier and NO client_secret (this is the public-client proof)..."
$tokenBody = @{
    grant_type = "authorization_code"
    code = $code
    redirect_uri = "http://localhost:5173/callback"
    client_id = "reactspa"
    code_verifier = $codeVerifier
}
$resp = Follow "https://localhost:5001/connect/token" "POST" $tokenBody
if ($resp.StatusCode -ne 200) { throw "Token endpoint rejected the public client: $($resp.Content)" }
if ($resp.Content -notmatch '"access_token"') { throw "No access_token in response: $($resp.Content)" }
Write-Host "   Token issued with no client secret sent - RequireClientSecret=false confirmed." -ForegroundColor Green

Write-Host "4. CORS preflight from the SPA's origin..."
$preflight = [System.Net.Http.HttpRequestMessage]::new("OPTIONS", "https://localhost:5001/connect/token")
$preflight.Headers.Add("Origin", "http://localhost:5173")
$preflight.Headers.Add("Access-Control-Request-Method", "POST")
$corsResp = $client.SendAsync($preflight).GetAwaiter().GetResult()
$allowOrigin = $corsResp.Headers.GetValues("Access-Control-Allow-Origin") 2>$null
if (-not $allowOrigin -or $allowOrigin[0] -ne "http://localhost:5173") { throw "Expected Access-Control-Allow-Origin: http://localhost:5173, got: $allowOrigin" }
Write-Host "   CORS preflight confirms localhost:5173 is allowed." -ForegroundColor Green

Write-Host ""
Write-Host "REACT SPA CLIENT (SERVER-SIDE HALF): PASS" -ForegroundColor Green
Write-Host "This proves the IdentityServer config is correct. It does not drive the actual React/oidc-client-ts" -ForegroundColor Yellow
Write-Host "code in a browser - do that manually: npm run dev, then click Log in at http://localhost:5173." -ForegroundColor Yellow
