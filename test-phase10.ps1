# Verifies the Mini.Infrastructure extraction. Phase 10 adds no features, so this script's job is the
# opposite of the usual one: prove that moving code between projects changed NOTHING observable, and that
# the one genuinely new thing (health endpoints) works.
#
# The real regression suite for this phase is the earlier scripts — test-phase3/4/7/9 must all still pass
# unmodified. Run them too.
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

function GetServiceAccountToken($tenantKey, $secret) {
    $client = NewClient
    $body = @{
        grant_type = "client_credentials"; client_id = "mvcclient-svc.$tenantKey"
        client_secret = $secret; scope = "api1"
    }
    $resp = Follow $client "https://localhost:5001/connect/token" "POST" $body
    if ($resp.StatusCode -ne 200) { throw "Client-credentials grant failed: $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function Call($uri, $token, $method = "GET") {
    $client = NewClient
    $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
    if ($token) {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    return @{
        StatusCode = [int]$response.StatusCode
        Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

Write-Host "1. Every host answers /health (new in this phase - run-all.ps1 depends on it)..."
$hosts = @(
    @{ Name = "IdentityServerHost"; Url = "https://localhost:5001/health" }
    @{ Name = "ExternalIdp";        Url = "https://localhost:5011/health" }
    @{ Name = "SampleApi";          Url = "https://localhost:5007/health" }
    @{ Name = "MvcClient";          Url = "https://localhost:5006/health" }
    @{ Name = "ExternalServicesStub"; Url = "https://localhost:5012/health" }
)
foreach ($h in $hosts) {
    $r = Call $h.Url $null
    if ($r.StatusCode -ne 200) { throw "$($h.Name) /health returned $($r.StatusCode), expected 200" }
    if ($r.Content -notmatch "healthy") { throw "$($h.Name) /health body was: $($r.Content)" }
}
Write-Host "   PASS - all five hosts healthy, unauthenticated" -ForegroundColor Green

Write-Host "2. IIdentityContext still identifies a USER correctly (now from Mini.Infrastructure)..."
$userToken = LoginAndGetToken "alice" "alice" "acme"
$r = Call "https://localhost:5007/api/v1/identity" $userToken
if ($r.StatusCode -ne 200) { throw "SampleApi returned $($r.StatusCode): $($r.Content)" }
$identity = ($r.Content | ConvertFrom-Json).identity
if ($identity.identityType -ne "User")  { throw "Expected identityType=User, got '$($identity.identityType)'" }
if ($identity.tenantKey -ne "acme")     { throw "Expected tenantKey=acme (from the tenant_id claim), got '$($identity.tenantKey)'" }
if (-not $identity.subject)             { throw "Expected a subject for a user identity" }
Write-Host "   PASS - identityType=User, tenantKey=acme, subject=$($identity.subject)" -ForegroundColor Green

Write-Host "3. ...and a SERVICE ACCOUNT correctly, with tenant parsed from the client_id suffix..."
$svcToken = GetServiceAccountToken "globex" "globex-svc-secret"
$r = Call "https://localhost:5007/api/v1/identity" $svcToken
if ($r.StatusCode -ne 200) { throw "SampleApi returned $($r.StatusCode): $($r.Content)" }
$identity = ($r.Content | ConvertFrom-Json).identity
if ($identity.identityType -ne "Service") { throw "Expected identityType=Service, got '$($identity.identityType)'" }
if ($identity.tenantKey -ne "globex")     { throw "Expected tenantKey=globex (parsed from client_id), got '$($identity.tenantKey)'" }
if ($identity.subject)                    { throw "A client-credentials token should have no subject, got '$($identity.subject)'" }
Write-Host "   PASS - identityType=Service, tenantKey=globex, no subject (that absence IS the signal)" -ForegroundColor Green

Write-Host "4. ServiceAccountOnlyFilter still discriminates (also moved)..."
$r = Call "https://localhost:5007/api/v1/admin/cache/globex" $svcToken "DELETE"
if ($r.StatusCode -ne 200) { throw "A service account should be allowed through, got $($r.StatusCode): $($r.Content)" }
$r = Call "https://localhost:5007/api/v1/admin/cache/acme" $userToken "DELETE"
if ($r.StatusCode -ne 403) { throw "A user should be forbidden (403), got $($r.StatusCode): $($r.Content)" }
$r = Call "https://localhost:5007/api/v1/admin/cache/acme" $null "DELETE"
if ($r.StatusCode -ne 401) { throw "An anonymous caller should be unauthorized (401), got $($r.StatusCode)" }
Write-Host "   PASS - service 200, user 403, anonymous 401" -ForegroundColor Green

Write-Host "5. The service-account token client still works end to end (MvcClient -> :5001 -> SampleApi)..."
# Both tenants have their own client registration and their own secret, which is the whole reason
# TokenClient builds client_id as "{ClientId}.{tenantKey}" instead of using one shared client.
foreach ($t in @(@{ Key = "acme"; Secret = "acme-svc-secret" }, @{ Key = "globex"; Secret = "globex-svc-secret" })) {
    $token = GetServiceAccountToken $t.Key $t.Secret
    $r = Call "https://localhost:5007/api/v1/identity" $token
    $tenantKey = ($r.Content | ConvertFrom-Json).identity.tenantKey
    if ($tenantKey -ne $t.Key) { throw "Expected tenantKey=$($t.Key), got '$tenantKey'" }
}
Write-Host "   PASS - per-tenant service accounts resolve to their own tenants" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 10 MINI.INFRASTRUCTURE EXTRACTION: PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Now run test-phase3.ps1, test-phase4.ps1, test-phase7.ps1 and test-phase9.ps1 -" -ForegroundColor DarkGray
Write-Host "they are the real regression suite for this phase and must pass unmodified." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Not scripted (needs a browser session against MvcClient): the tenant-registry drift." -ForegroundColor DarkGray
Write-Host "Phase 9 added 'initech' to IdentityServerHost and ExternalServicesStub, but NOT to" -ForegroundColor DarkGray
Write-Host "MvcClient's own Tenants.cs. Log into MvcClient as an Initech user and /Home/Secure" -ForegroundColor DarkGray
Write-Host "returns 401 from RequireTenantAttribute - three registries agreeing only by convention," -ForegroundColor DarkGray
Write-Host "which is exactly the real Apply-vs-IdG failure mode. Left in place on purpose." -ForegroundColor DarkGray
