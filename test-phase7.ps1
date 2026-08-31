# Verifies Phase 7: IdentityServerHost calls two real DIT sibling services (ExternalServicesStub,
# standing in for a Tenant Management API and a User API) at token-issuance time, using a self-issued
# JWT it mints for itself - no client secret, no /connect/token round trip for this hop.
#
# Run IdentityServerHost, ExternalIdp, MvcClient, SampleApi and ExternalServicesStub first (see the
# root README's "Running it" section).

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
    $tokenBody = @{
        grant_type = "authorization_code"; code = $code
        redirect_uri = "http://localhost:5173/callback"; client_id = "reactspa"; code_verifier = $pkce.Verifier
    }
    $resp = Follow $client "https://localhost:5001/connect/token" "POST" $tokenBody
    if ($resp.StatusCode -ne 200) { throw "Token endpoint failed: $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function DecodeJwtPayload($jwt) {
    $payload = $jwt.Split('.')[1]
    $payload += ('=' * ((4 - $payload.Length % 4) % 4))
    $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.Replace('-', '+').Replace('_', '/')))
    return $json | ConvertFrom-Json
}

Write-Host "1. alice (tenant:acme) - tenant_guid and role should come from ExternalServicesStub..." -ForegroundColor Cyan
$aliceToken = LoginAndGetToken "alice" "alice" "acme"
$aliceClaims = DecodeJwtPayload $aliceToken
if ($aliceClaims.tenant_guid -ne "8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") { throw "Expected acme's tenant_guid, got: $($aliceClaims.tenant_guid)" }
if ($aliceClaims.role -ne "Admin") { throw "Expected role=Admin for alice (subject '1'), got: $($aliceClaims.role)" }
Write-Host "   PASS - tenant_guid=$($aliceClaims.tenant_guid), role=$($aliceClaims.role)" -ForegroundColor Green

Write-Host ""
Write-Host "2. bob (tenant:globex) - a different tenant_guid, and role defaults to Member (not in the stub's table)..." -ForegroundColor Cyan
$bobToken = LoginAndGetToken "bob" "bob" "globex"
$bobClaims = DecodeJwtPayload $bobToken
if ($bobClaims.tenant_guid -ne "c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e") { throw "Expected globex's tenant_guid, got: $($bobClaims.tenant_guid)" }
if ($bobClaims.role -ne "Member") { throw "Expected role=Member (default) for bob, got: $($bobClaims.role)" }
Write-Host "   PASS - tenant_guid=$($bobClaims.tenant_guid), role=$($bobClaims.role)" -ForegroundColor Green

Write-Host ""
Write-Host "3. Confirm these claims also reach SampleApi's access token (api1's apiResources userClaims)..." -ForegroundColor Cyan
$request = [System.Net.Http.HttpRequestMessage]::new("GET", "https://localhost:5007/api/v1/identity")
$request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $aliceToken)
$httpClient = New-Object System.Net.Http.HttpClient
$response = $httpClient.SendAsync($request).GetAwaiter().GetResult()
$body = ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json)
$roleClaim = $body.claims | Where-Object { $_.Type -eq "role" }
$tenantGuidClaim = $body.claims | Where-Object { $_.Type -eq "tenant_guid" }
if ($response.StatusCode -ne 200) { throw "SampleApi call failed: $($response.StatusCode)" }
if (-not $roleClaim -or $roleClaim.Value -ne "Admin") { throw "Expected role=Admin on SampleApi's access token too" }
if (-not $tenantGuidClaim) { throw "Expected tenant_guid on SampleApi's access token too" }
Write-Host "   PASS - SampleApi independently validated the same access token and saw tenant_guid/role too" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 7 DIT EXTERNAL-SERVICE CALLS: PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Not scripted (needs editing code, not just data): the never-expiring tenant_guid cache bug." -ForegroundColor Yellow
Write-Host "Try it yourself - change acme's GUID in ExternalServicesStub/Program.cs, restart ONLY that" -ForegroundColor Yellow
Write-Host "project (not IdentityServerHost), and log in as alice again: tenant_guid is still the OLD" -ForegroundColor Yellow
Write-Host "value, because IdentityServerHost's own in-memory cache never expires. Change a role in the" -ForegroundColor Yellow
Write-Host "same file instead and it takes effect on the very next login - role is never cached at all." -ForegroundColor Yellow
