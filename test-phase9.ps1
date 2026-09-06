# Verifies the IdentityProviderStore: an external identity provider configured ONLY as a row in the
# IdentityProviders table (never in appsettings.json) shows up on its tenant's login page and can complete
# a real federated login through Duende's dynamic-provider path (/federation/{scheme}/...).
#
# Run ExternalIdp (5011) and IdentityServerHost (5001) first, and run src/Tools/ConfigIngestionTool once
# so the initech-external-idp row exists. Then run this script.
#
# Helpers below are lifted from test-phase4.ps1 unchanged — same PKCE pair, same redirect-following.

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

Write-Host "1. Initech's login page should show an external option that exists ONLY in the database..."
$client = NewClient
$r = GetLoginPage $client "initech"
if ($r.Response.Content -notmatch "ExternalIdp \(Initech SSO, from the database\)") {
    throw "Initech should see the database-backed provider. Did you run ConfigIngestionTool? Got: $($r.Response.Content.Substring(0, [Math]::Min(500, $r.Response.Content.Length)))"
}
Write-Host "   PASS - the row in IdentityProviders became a login button" -ForegroundColor Green

Write-Host "2. The other two tenants are unchanged by this phase..."
$acme = GetLoginPage (NewClient) "acme"
if ($acme.Response.Content -notmatch "Sign in with ExternalIdp \(partner SSO\)") { throw "Acme lost its file-based provider" }
if ($acme.Response.Content -match "from the database") { throw "Acme should not see Initech's database-backed provider" }
$globex = GetLoginPage (NewClient) "globex"
if ($globex.Response.Content -match "Sign in with") { throw "Globex should still see no external option at all" }
Write-Host "   PASS - acme still file-based only, globex still none (tenant filtering holds across both sources)" -ForegroundColor Green

Write-Host "3. Complete a real federated login through the DYNAMIC provider path..."
$challengeHref = [regex]::Match($r.Response.Content, 'href="(/External/Challenge[^"]*initech-external-idp[^"]*)"').Groups[1].Value
$challengeHref = [System.Net.WebUtility]::HtmlDecode($challengeHref)
if (-not $challengeHref) { throw "Could not find Initech's External/Challenge link on the login page" }

$resp = Follow $client "https://localhost:5001$challengeHref"
if ($resp.Content -notmatch "Sign in to ExternalIdp") {
    throw "Expected ExternalIdp's login page, got: $($resp.Content.Substring(0, [Math]::Min(400, $resp.Content.Length)))"
}
Write-Host "   Reached ExternalIdp via /federation/initech-external-idp/ - a scheme that did not exist at startup." -ForegroundColor Green

$extReturnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$extVerToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
$body = @{ Username = "carol"; Password = "carol"; ReturnUrl = $extReturnUrl; __RequestVerificationToken = $extVerToken }
$resp = Follow $client "https://localhost:5011/Account/Login" "POST" $body "localhost:5173"

$formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value)
if ($formAction) {
    $hiddenFields = @{}
    [regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object {
        $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value)
    }
    $resp = Follow $client $formAction "POST" $hiddenFields "localhost:5173"
}

if ($resp.Uri -notmatch "code=") {
    throw "Expected to land back at reactspa's redirect_uri with a code, got: $($resp.Uri)"
}
Write-Host "   PASS - round trip completed back through mini-idg to reactspa's callback" -ForegroundColor Green

Write-Host "4. Confirm the claims: Carol's name from ExternalIdp, tenant_id=initech from the request..."
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

if ($claims.name -ne "Carol Chen") { throw "Expected name='Carol Chen' (from ExternalIdp), got: $($claims.name)" }
if ($claims.tenant_id -ne "initech") { throw "Expected tenant_id='initech' (from the original request), got: $($claims.tenant_id)" }
Write-Host "   PASS - name=$($claims.name), tenant_id=$($claims.tenant_id)" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 9 IDENTITYPROVIDERSTORE: PASS" -ForegroundColor Green
