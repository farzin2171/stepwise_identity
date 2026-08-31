# Verifies per-tenant external IdP: Acme sees a "Sign in with ExternalIdp" link and can complete a real
# federated login (through a second, independent Duende IdentityServer); Globex sees no such link at all.
# Run ExternalIdp (port 5010) and IdentityServerHost (port 5000) first, then run this script.

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

function NewClient() {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    return New-Object System.Net.Http.HttpClient($handler)
}

function GetLoginPage($client, $tenantHint) {
    $pkce = NewPkcePair
    $authorizeUrl = "https://localhost:5001/connect/authorize?client_id=reactspa&redirect_uri=" + `
        [uri]::EscapeDataString("http://localhost:5173/callback") + `
        "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile tenant") + `
        "&code_challenge=$($pkce.Challenge)&code_challenge_method=S256&state=teststate123" + `
        "&acr_values=" + [uri]::EscapeDataString("tenant:$tenantHint")
    $resp = Follow $client $authorizeUrl
    return @{ Response = $resp; Pkce = $pkce }
}

Write-Host "1. Globex's login page should show NO external sign-in option..."
$client = NewClient
$r = GetLoginPage $client "globex"
if ($r.Response.Content -match "Sign in with") { throw "Globex should not see any external IdP option" }
Write-Host "   PASS - no external option shown for Globex" -ForegroundColor Green

Write-Host "2. Acme's login page SHOULD show the ExternalIdp option..."
$client = NewClient
$r = GetLoginPage $client "acme"
if ($r.Response.Content -notmatch "Sign in with ExternalIdp") { throw "Acme should see the ExternalIdp option, got: $($r.Response.Content.Substring(0, 400))" }
Write-Host "   PASS - ExternalIdp option shown for Acme" -ForegroundColor Green

$challengeHref = [regex]::Match($r.Response.Content, 'href="(/External/Challenge[^"]*)"').Groups[1].Value
$challengeHref = [System.Net.WebUtility]::HtmlDecode($challengeHref)
if (-not $challengeHref) { throw "Could not find the External/Challenge link on the login page" }

Write-Host "3. Click through the federated login: mini-idg -> ExternalIdp -> back..."
$resp = Follow $client "https://localhost:5001$challengeHref"
if ($resp.Content -notmatch "Sign in to ExternalIdp") { throw "Expected to land on ExternalIdp's own login page, got: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Landed on ExternalIdp's login page (a completely separate server)." -ForegroundColor Green

$extReturnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$extVerToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
$body = @{ Username = "carol"; Password = "carol"; ReturnUrl = $extReturnUrl; __RequestVerificationToken = $extVerToken }
$resp = Follow $client "https://localhost:5011/Account/Login" "POST" $body "localhost:5173"

# ExternalIdp's own authorize callback defaults to response_mode=form_post too (same library default as
# MvcClient) - one more auto-post form to submit by hand before this continues.
$formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value)
if ($formAction) {
    $hiddenFields = @{}
    [regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object {
        $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value)
    }
    $resp = Follow $client $formAction "POST" $hiddenFields "localhost:5173"
}

# This lands back inside mini-idg's own /connect/authorize/callback flow, eventually reaching reactspa's
# redirect_uri with an authorization code - same shape as test-phase3.ps1 from here.
if ($resp.Uri -notmatch "code=") { throw "Expected the flow to complete back at reactspa's redirect_uri with a code, landed on: $($resp.Uri) content: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Completed the round trip back to mini-idg and on to reactspa's callback." -ForegroundColor Green

$code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]
$tokenBody = @{
    grant_type = "authorization_code"; code = $code
    redirect_uri = "http://localhost:5173/callback"; client_id = "reactspa"; code_verifier = $r.Pkce.Verifier
}
$resp = Follow $client "https://localhost:5001/connect/token" "POST" $tokenBody
if ($resp.StatusCode -ne 200) { throw "Token endpoint failed: $($resp.Content)" }

$accessToken = ($resp.Content | ConvertFrom-Json).access_token
$userinfoRequest = [System.Net.Http.HttpRequestMessage]::new("GET", "https://localhost:5001/connect/userinfo")
$userinfoRequest.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $accessToken)
$userinfoResp = $client.SendAsync($userinfoRequest).GetAwaiter().GetResult()
$claims = ($userinfoResp.Content.ReadAsStringAsync().GetAwaiter().GetResult()) | ConvertFrom-Json

Write-Host "4. Confirm the claims: Carol's name (from ExternalIdp) + tenant_id=acme (from the request, not ExternalIdp)..."
if ($claims.name -ne "Carol Chen") { throw "Expected name='Carol Chen' (from ExternalIdp), got: $($claims.name)" }
if ($claims.tenant_id -ne "acme") { throw "Expected tenant_id='acme' (from the original request), got: $($claims.tenant_id)" }
Write-Host "   PASS - name=$($claims.name), tenant_id=$($claims.tenant_id)" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 4 EXTERNAL IDP FEDERATION: PASS" -ForegroundColor Green
