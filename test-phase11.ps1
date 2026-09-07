# Verifies Phase 11: Mini.UserService (:5013) replaces ExternalServicesStub (:5012) with a real service
# — its own SQL database, an authenticated management API, and an outbound call of its own back into
# IdentityServerHost.
#
# Start everything with .\run-all.ps1 first. Note that ExternalServicesStub is NOT in the default set
# any more; this script asserts that.
#
# The real regression suite for this phase is test-phase7.ps1, which must pass UNMODIFIED — it was
# written against the stub and now exercises this service instead. That, not anything in this file, is
# the proof that the replacement is behaviour-preserving.

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

function GetClientCredentialsToken($clientId, $secret, $scope) {
    $resp = Follow (NewClient) "https://localhost:5001/connect/token" "POST" @{
        grant_type = "client_credentials"; client_id = $clientId; client_secret = $secret; scope = $scope
    }
    if ($resp.StatusCode -ne 200) { throw "Client-credentials grant failed for '$clientId': $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function LoginAndGetUserToken($username, $password, $tenantKey) {
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

# Drives the full federated login through ExternalIdp so there is a provisioned external identity to
# convert in section 6. Same flow as test-phase4.ps1 - reproduced rather than shared because these
# scripts are deliberately standalone.
function ProvisionFederatedIdentity($tenantKey, $expectedLinkText) {
    $client = NewClient
    $pkce = NewPkcePair
    $authorizeUrl = "https://localhost:5001/connect/authorize?client_id=reactspa&redirect_uri=" + `
        [uri]::EscapeDataString("http://localhost:5173/callback") + `
        "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile tenant") + `
        "&code_challenge=$($pkce.Challenge)&code_challenge_method=S256&state=teststate123" + `
        "&acr_values=" + [uri]::EscapeDataString("tenant:$tenantKey")
    $resp = Follow $client $authorizeUrl
    if ($resp.Content -notmatch [regex]::Escape($expectedLinkText)) {
        throw "Expected '$expectedLinkText' on $tenantKey's login page"
    }

    $challengeHref = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'href="(/External/Challenge[^"]*)"').Groups[1].Value)
    if (-not $challengeHref) { throw "Could not find the External/Challenge link for $tenantKey" }

    $resp = Follow $client "https://localhost:5001$challengeHref"
    $extReturnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
    $extVerToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
    $resp = Follow $client "https://localhost:5011/Account/Login" "POST" `
        @{ Username = "carol"; Password = "carol"; ReturnUrl = $extReturnUrl; __RequestVerificationToken = $extVerToken } "localhost:5173"

    $formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['`"]([^'`"]*)['`"]").Groups[1].Value)
    if ($formAction) {
        $hiddenFields = @{}
        [regex]::Matches($resp.Content, "name=['`"]([^'`"]+)['`"] value=['`"]([^'`"]*)['`"]") | ForEach-Object {
            $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value)
        }
        $resp = Follow $client $formAction "POST" $hiddenFields "localhost:5173"
    }
    if ($resp.Uri -notmatch "code=") { throw "Federated login for $tenantKey did not complete: $($resp.Uri)" }
}

Write-Host "1. Mini.UserService is up on :5013, and the superseded stub is NOT in the default set..." -ForegroundColor Cyan
$r = Call "https://localhost:5013/health" $null
if ($r.StatusCode -ne 200) { throw "Mini.UserService /health returned $($r.StatusCode)" }
if ($r.Content -notmatch "healthy") { throw "Mini.UserService /health body was: $($r.Content)" }
$stubIsUp = $true
try { Call "https://localhost:5012/health" $null | Out-Null } catch { $stubIsUp = $false }
if ($stubIsUp) {
    Write-Host "   NOTE - something is answering on :5012. Started with -IncludeStub? That's fine." -ForegroundColor Yellow
}
Write-Host "   PASS - :5013 healthy; the stub is no longer on any path a login depends on" -ForegroundColor Green

Write-Host "2. The tenant registry is a DATABASE now, not a Dictionary literal..." -ForegroundColor Cyan
# Same GUIDs the stub hardcoded, now rows in MiniUsers seeded through the migration. -cne, because the
# case is the point: see TenantEndpoints.cs for the SQL-translation bug that made this assertion
# necessary, and note that PowerShell's plain -ne would NOT have caught it.
$mgmtToken = GetClientCredentialsToken "userservice-mgmt-svc" "userservice-mgmt-secret" "tenantmgntapi userapi"
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/acme" $mgmtToken
if ($r.StatusCode -ne 200) { throw "Expected 200 reading acme, got $($r.StatusCode): $($r.Content)" }
$acme = $r.Content | ConvertFrom-Json
if ($acme.tenantId -cne "8f14e45f-ceea-467e-bd42-05d1a4a6b3f0") {
    throw "Expected acme's GUID lowercase (byte-identical to the stub's), got: $($acme.tenantId)"
}
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/nosuchtenant" $mgmtToken
if ($r.StatusCode -ne 404) { throw "An unknown tenant key should 404, got $($r.StatusCode)" }
Write-Host "   PASS - acme=$($acme.tenantId) from SQL; unknown key 404s" -ForegroundColor Green

Write-Host "3. The two collapsed services keep separate audiences..." -ForegroundColor Cyan
# One process, two API surfaces. A user's own access token (aud=api1) is not merely under-privileged
# here - it fails token validation outright, because "api1" is not an audience this service accepts.
$userToken = LoginAndGetUserToken "alice" "alice" "acme"
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/acme" $userToken
if ($r.StatusCode -ne 401) { throw "A user token (aud=api1) should be rejected, got $($r.StatusCode)" }
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/acme" $null
if ($r.StatusCode -ne 401) { throw "An anonymous caller should be 401, got $($r.StatusCode)" }
# A conversion service account holds only "IdentityServerApi" - it can call back into the IdG and
# nothing else. Its audience is Duende's default "{issuer}/resources", not "tenantmgntapi".
$convertToken = GetClientCredentialsToken "userservice-svc.acme" "acme-userservice-secret" "IdentityServerApi"
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/acme" $convertToken
if ($r.StatusCode -ne 401) { throw "userservice-svc.acme has no tenantmgntapi audience; expected 401, got $($r.StatusCode)" }
Write-Host "   PASS - user token 401, anonymous 401, wrong-audience service account 401" -ForegroundColor Green

Write-Host "4. A tenant can be onboarded over HTTP, with no code edit and no restart..." -ForegroundColor Cyan
# Phase 10's "try it yourself" asked which of the three tenant registries should be authoritative. This
# is the first one that can be written to at runtime. A fresh key per run, so the script is repeatable.
$newKey = "phase11-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$r = Call "https://localhost:5013/api/v1/management/tenants" $mgmtToken "POST" `
    "{""key"":""$newKey"",""name"":""Phase 11 Test Tenant"",""description"":""created by test-phase11.ps1""}"
if ($r.StatusCode -ne 201) { throw "Expected 201 creating a tenant, got $($r.StatusCode): $($r.Content)" }
$created = $r.Content | ConvertFrom-Json
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/$newKey" $mgmtToken
if ($r.StatusCode -ne 200) { throw "The tenant just created should be readable, got $($r.StatusCode)" }
if (($r.Content | ConvertFrom-Json).tenantId -ne $created.tenantId) { throw "Read back a different tenantId" }
$r = Call "https://localhost:5013/api/v1/management/tenants" $mgmtToken "POST" `
    "{""key"":""$newKey"",""name"":""Duplicate""}"
if ($r.StatusCode -ne 409) { throw "Creating the same key twice should 409, got $($r.StatusCode)" }
# Hard-deleted again so repeated runs don't accumulate rows. The real Services.TenantManagement
# hard-deletes on its management API too (and soft-deletes on the read API - a split its own analysis
# flags as possibly unintentional).
$r = Call "https://localhost:5013/api/v1/management/tenants/$newKey" $mgmtToken "DELETE"
if ($r.StatusCode -ne 204) { throw "Deleting the tenant should 204, got $($r.StatusCode): $($r.Content)" }
$r = Call "https://localhost:5013/api/v1/tenants/GetByKey/$newKey" $mgmtToken
if ($r.StatusCode -ne 404) { throw "The deleted tenant should 404 now, got $($r.StatusCode)" }
Write-Host "   PASS - created '$newKey' ($($created.tenantId)), read it back, duplicate 409s, deleted 204" -ForegroundColor Green

Write-Host "5. The management API needs a REGISTERED service account, not just any service token..." -ForegroundColor Cyan
$r = Call "https://localhost:5013/api/v1/management/tenants" $userToken "POST" '{"key":"nope","name":"Nope"}'
if ($r.StatusCode -ne 401) { throw "A user token should not reach the management API, got $($r.StatusCode)" }
$r = Call "https://localhost:5013/api/v1/management/tenants" $null "POST" '{"key":"nope","name":"Nope"}'
if ($r.StatusCode -ne 401) { throw "An anonymous caller should be 401, got $($r.StatusCode)" }
$r = Call "https://localhost:5013/api/v1/management/tenants" $convertToken "POST" '{"key":"nope","name":"Nope"}'
if ($r.StatusCode -ne 401) { throw "A service account without the tenantmgntapi audience should be refused, got $($r.StatusCode)" }
# Roles are runtime-writable too - and unlike tenant_guid, the role claim is never cached, so the next
# login picks this up immediately.
$r = Call "https://localhost:5013/api/v2/management/user/identities/role/2" $mgmtToken "PUT" '{"role":"Auditor"}'
if ($r.StatusCode -ne 200) { throw "Assigning a role should 200, got $($r.StatusCode): $($r.Content)" }
$r = Call "https://localhost:5013/api/v2/User/identities/role/2" $mgmtToken
if ($r.Content.Trim() -ne "Auditor") { throw "Expected bob's role to be Auditor now, got: $($r.Content)" }
# Deleted again, so test-phase7.ps1 (which asserts bob FALLS BACK to Member, with no row at all) keeps
# passing. Writing "Member" back would leave a row behind and quietly change what that test proves.
$r = Call "https://localhost:5013/api/v2/management/user/identities/role/2" $mgmtToken "DELETE"
if ($r.StatusCode -ne 204) { throw "Deleting the role row should 204, got $($r.StatusCode): $($r.Content)" }
$r = Call "https://localhost:5013/api/v2/User/identities/role/2" $mgmtToken
if ($r.Content.Trim() -ne "Member") { throw "Expected bob to fall back to Member again, got: $($r.Content)" }
Write-Host "   PASS - management refuses user/anonymous/wrong-audience; role write takes effect at once" -ForegroundColor Green

Write-Host "6. The dependency is BIDIRECTIONAL: Mini.UserService calls back into IdentityServerHost..." -ForegroundColor Cyan
ProvisionFederatedIdentity "acme" "Sign in with ExternalIdp"
$localSubjectId = "external:external-idp:ext-1"
# Through Mini.UserService, which acquires a per-tenant service-account token (userservice-svc.acme)
# and calls the IdG's own /api/user/convert endpoint with it.
$r = Call ("https://localhost:5013/api/v2/useridentity/convert/" + [uri]::EscapeDataString($localSubjectId) + "?conversionType=External&tenant=acme") $mgmtToken
if ($r.StatusCode -ne 200) { throw "Conversion through Mini.UserService failed with $($r.StatusCode): $($r.Content)" }
$converted = $r.Content | ConvertFrom-Json
if ($converted.convertedUserId -ne "ext-1") { throw "Expected convertedUserId=ext-1, got: $($converted.convertedUserId)" }
$r = Call "https://localhost:5013/api/v2/useridentity/convert/1?conversionType=Sideways&tenant=acme" $mgmtToken
if ($r.StatusCode -ne 400) { throw "An unknown conversionType should 400, got $($r.StatusCode)" }
$r = Call "https://localhost:5013/api/v2/useridentity/convert/1?conversionType=External&tenant=acme" $mgmtToken
if ($r.StatusCode -ne 404) { throw "A local password user has no external identity; expected 404, got $($r.StatusCode)" }
Write-Host "   PASS - $localSubjectId -> ext-1, via a real client-credentials token" -ForegroundColor Green

Write-Host "7. The IdG's own convert endpoint is scope-gated (Duende local API)..." -ForegroundColor Cyan
$convertUrl = "https://localhost:5001/api/user/convert/" + [uri]::EscapeDataString($localSubjectId) + "?convertTo=External"
$r = Call $convertUrl $convertToken
if ($r.StatusCode -ne 200) { throw "userservice-svc.acme should reach the convert endpoint, got $($r.StatusCode): $($r.Content)" }
if (($r.Content | ConvertFrom-Json).provider -ne "external-idp") { throw "Expected provider=external-idp" }
$r = Call $convertUrl $null
if ($r.StatusCode -ne 401) { throw "Anonymous should be 401, got $($r.StatusCode)" }
# 401, NOT 403. Duende's local-API handler checks the expected scope during AUTHENTICATION, so a valid
# token without "IdentityServerApi" is indistinguishable from no token at all - unlike SampleApi, where
# JwtBearer authenticates first and a policy then forbids with 403.
$r = Call $convertUrl $mgmtToken
if ($r.StatusCode -ne 401) { throw "A token without the IdentityServerApi scope should be 401, got $($r.StatusCode)" }
$r = Call $convertUrl $userToken
if ($r.StatusCode -ne 401) { throw "A user token should be 401, got $($r.StatusCode)" }
Write-Host "   PASS - correct scope 200; anonymous, wrong-scope and user tokens all 401" -ForegroundColor Green

Write-Host "8. Two providers issued the same external subject id, and the API says so..." -ForegroundColor Cyan
# Not a contrived case: ExternalIdp is the login source for BOTH acme (external-idp) and initech
# (initech-external-idp), and carol is "ext-1" at both. Converting external -> local from the subject id
# alone therefore has two answers, and this sample's composite key ("external:{scheme}:{subjectId}")
# gives the lookup no way to pick. The real IdG stores provider and subject in separate columns and
# never has to.
ProvisionFederatedIdentity "initech" "ExternalIdp (Initech SSO, from the database)"
$r = Call "https://localhost:5001/api/user/convert/ext-1?convertTo=Local" $convertToken
if ($r.StatusCode -ne 409) { throw "Expected 409 for an ambiguous external subject id, got $($r.StatusCode): $($r.Content)" }
Write-Host "   PASS - 409 Conflict, with the reason named rather than a silently-picked row" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 11 MINI.USERSERVICE: PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Now run test-phase7.ps1. It was written against ExternalServicesStub and must pass" -ForegroundColor DarkGray
Write-Host "UNMODIFIED against this service - that is the actual proof the replacement preserves" -ForegroundColor DarkGray
Write-Host "behaviour. test-phase2/3/4/5/6/9/10 and test-api are the rest of the regression suite." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Not scripted (needs editing code, not just data): the self-issued-JWT hole." -ForegroundColor Yellow
Write-Host "IdentityServerHost's read token has no 'sub', so ServiceAccountOnlyFilter classifies it as" -ForegroundColor Yellow
Write-Host "a service account - the same verdict a registered service account gets. To see it:" -ForegroundColor Yellow
Write-Host "  1. In Mini.UserService/Endpoints/ManagementEndpoints.cs, add a GET to the same group:" -ForegroundColor Yellow
Write-Host "       userManagement.MapGet('/probe', () => Results.Text('REACHED'));" -ForegroundColor Yellow
Write-Host "  2. Point IdentityServerHost/ExternalServices/UserClient.cs at /v2/management/probe." -ForegroundColor Yellow
Write-Host "  3. Restart, log in, and read the 'role' claim - it holds whatever the probe returned." -ForegroundColor Yellow
Write-Host "With the group on 'UserApiManagement' you get 403. Change it to 'UserApi' and the" -ForegroundColor Yellow
Write-Host "self-issued read token walks straight into the management API. The scope claim is the only" -ForegroundColor Yellow
Write-Host "thing separating them - see Mini.UserService/Program.cs." -ForegroundColor Yellow
