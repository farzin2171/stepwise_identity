# Verifies the server-side half of ReactSpa's "Call the API" button: logs in as the "reactspa" public
# client requesting the "api1" scope, then calls SampleApi exactly as the browser's fetch() would —
# including the CORS preflight and the Origin header on the real request. Does NOT drive real
# React/oidc-client-ts code in a browser; see ReactSpa's README for what that leaves unverified.
# Run IdentityServerHost and SampleApi first (dotnet run in each), then run this script.

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

Write-Host "1. Log in as alice as the reactspa public client, requesting 'openid profile api1'..."
$authorizeUrl = "http://localhost:5000/connect/authorize?client_id=reactspa&redirect_uri=" + `
    [uri]::EscapeDataString("http://localhost:5173/callback") + `
    "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile api1") + `
    "&code_challenge=$codeChallenge&code_challenge_method=S256&state=teststate123"
$resp = Follow $authorizeUrl
if ($resp.Content -notmatch "Sign in") { throw "Expected the login page: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
if (-not $returnUrl -or -not $verToken) { throw "Could not parse ReturnUrl or antiforgery token from login page" }
$resp = Follow "http://localhost:5000/Account/Login" "POST" @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken } "localhost:5173"
if ($resp.Uri -notmatch "code=") { throw "Expected the final redirect to carry ?code=..., landed on: $($resp.Uri)" }
$code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]
Write-Host "   Got an authorization code." -ForegroundColor Green

$resp = Follow "http://localhost:5000/connect/token" "POST" @{
    grant_type = "authorization_code"; code = $code; redirect_uri = "http://localhost:5173/callback"
    client_id = "reactspa"; code_verifier = $codeVerifier
}
if ($resp.StatusCode -ne 200) { throw "Token endpoint rejected the public client: $($resp.Content)" }
$accessToken = ($resp.Content | ConvertFrom-Json).access_token
Write-Host "   Got an access token (no client secret sent)." -ForegroundColor Green

Write-Host "2. CORS preflight for GET /api/identity from the SPA's origin..."
$preflight = [System.Net.Http.HttpRequestMessage]::new("OPTIONS", "http://localhost:5003/api/identity")
$preflight.Headers.Add("Origin", "http://localhost:5173")
$preflight.Headers.Add("Access-Control-Request-Method", "GET")
$preflight.Headers.Add("Access-Control-Request-Headers", "authorization")
$corsResp = $client.SendAsync($preflight).GetAwaiter().GetResult()
$allowOrigin = $corsResp.Headers.GetValues("Access-Control-Allow-Origin") 2>$null
if (-not $allowOrigin -or $allowOrigin[0] -ne "http://localhost:5173") { throw "Expected Access-Control-Allow-Origin: http://localhost:5173 from SampleApi, got: $allowOrigin" }
Write-Host "   SampleApi's CORS policy allows localhost:5173." -ForegroundColor Green

Write-Host "3. The actual cross-origin GET, exactly as the browser's fetch() would send it..."
$apiRequest = [System.Net.Http.HttpRequestMessage]::new("GET", "http://localhost:5003/api/identity")
$apiRequest.Headers.Add("Origin", "http://localhost:5173")
$apiRequest.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $accessToken)
$apiResp = $client.SendAsync($apiRequest).GetAwaiter().GetResult()
$apiBody = $apiResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
if ([int]$apiResp.StatusCode -ne 200) { throw "Expected HTTP 200 from SampleApi, got $([int]$apiResp.StatusCode): $apiBody" }
if ($apiBody -notmatch "api1") { throw "Expected the 'api1' scope/audience claim in SampleApi's response" }
if ($apiBody -notmatch "Alice Anderson") { throw "Expected the 'name' claim to reach SampleApi via the access token" }
Write-Host "   SampleApi answered HTTP 200 with alice's claims, including 'api1'." -ForegroundColor Green

Write-Host ""
Write-Host "REACT SPA CALLING SAMPLEAPI (SERVER-SIDE HALF): PASS" -ForegroundColor Green
Write-Host "This proves IdentityServer + SampleApi's config (scope, CORS, JWT validation) is correct. It" -ForegroundColor Yellow
Write-Host "does not click the real button in a browser - do that manually at http://localhost:5173." -ForegroundColor Yellow
