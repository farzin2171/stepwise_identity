# Verifies Phase 12: per-tenant, cascading connectors. The role claim no longer comes from one table
# for every tenant - it comes from whatever that tenant's connector rows say it comes from.
#
# Start everything with .\run-all.ps1 first. Mini.AcmeApi (:5014) is in the default set now and is on
# acme's login path, so a login's role claim depends on a process standing in for a system the TENANT
# owns. Section 7 exploits that on purpose.
#
# The regression suite for this phase is test-phase7.ps1, which must pass UNMODIFIED. It asserts
# alice's role is "Admin", and it still is - but as of this phase that answer travels out of Acme's own
# web API instead of out of a row in MiniUsers. Holding the value constant while the mechanism moves is
# the same trick Phase 11 played with the tenant GUIDs, and for the same reason: it is what makes
# "the source changed" a provable claim rather than a hope.

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
    $body = @{ grant_type = "client_credentials"; client_id = $clientId; client_secret = $secret }
    if ($scope) { $body.scope = $scope }
    $resp = Follow (NewClient) "https://localhost:5001/connect/token" "POST" $body
    if ($resp.StatusCode -ne 200) { throw "Client-credentials grant failed for '$clientId': $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function LoginAndGetClaims($username, $password, $tenantKey) {
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

    # The ACCESS token, not the id_token - same choice test-phase7.ps1 makes, and it matters. "role"
    # and "tenant_guid" ride on the access token because api1's apiResource lists them in userClaims;
    # they are NOT on the id_token unless the client sets AlwaysIncludeUserClaimsInIdToken. Decoding
    # the wrong one here gives an empty role and looks exactly like a broken connector.
    $accessToken = ($resp.Content | ConvertFrom-Json).access_token
    $payload = ($accessToken -split '\.')[1].Replace('-', '+').Replace('_', '/')
    while ($payload.Length % 4) { $payload += '=' }
    return [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
}

# Drives the full federated login through ExternalIdp so carol exists as a provisioned external
# identity. Same flow as test-phase4.ps1 / test-phase11.ps1 - reproduced rather than shared because
# these scripts are deliberately standalone.
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

$roleUrl = "https://localhost:5013/api/v2/User/identities/role"
$emailUrl = "https://localhost:5013/api/v2/User/identities/email"
$carolLocalId = "external:external-idp:ext-1"

$mgmtToken = GetClientCredentialsToken "userservice-mgmt-svc" "userservice-mgmt-secret" "tenantmgntapi userapi"

Write-Host "1. Acme's own user API (:5014) is up, and it is somebody ELSE's system..." -ForegroundColor Cyan
$r = Call "https://localhost:5014/health" $null
if ($r.StatusCode -ne 200) { throw "Mini.AcmeApi /health returned $($r.StatusCode)" }
$r = Call "https://localhost:5014/users/1" $null
if ($r.StatusCode -ne 401) { throw "Anonymous should be 401 at Acme's API, got $($r.StatusCode)" }
# The audience gets you through the door; the client_id decides whether you may read anything. All
# three userservice-svc.{tenant} clients can request "acmeapi", so all three hold a token Acme
# ACCEPTS - and only Acme's own gets past its authorization policy.
$acmeSvc = GetClientCredentialsToken "userservice-svc.acme" "acme-userservice-secret" "acmeapi"
$initechSvc = GetClientCredentialsToken "userservice-svc.initech" "initech-userservice-secret" "acmeapi"
$r = Call "https://localhost:5014/users/1" $acmeSvc
if ($r.StatusCode -ne 200) { throw "Acme's own service account should be 200, got $($r.StatusCode): $($r.Content)" }
if (($r.Content | ConvertFrom-Json).role -ne "Admin") { throw "Acme's API should say alice is Admin" }
$r = Call "https://localhost:5014/users/1" $initechSvc
if ($r.StatusCode -ne 403) { throw "Initech's service account should be 403 at Acme's API, got $($r.StatusCode)" }
# 403, not 401: the token validated fine (right issuer, right audience) and then failed
# authorization. A scope says which RESOURCE you may reach and cannot say whose data within it.
$r = Call "https://localhost:5014/users/1" $mgmtToken
if ($r.StatusCode -ne 401) { throw "A token with no acmeapi audience should be 401, got $($r.StatusCode)" }
Write-Host "   PASS - anonymous 401, wrong-audience 401, wrong TENANT 403, acme 200" -ForegroundColor Green

Write-Host "2. acme cascades: its own web API answers, and the value is provably not the local table's..." -ForegroundColor Cyan
ProvisionFederatedIdentity "acme" "Sign in with ExternalIdp"
# alice has a row in MiniUsers saying Admin AND Acme's API says Admin - byte-identical on purpose, so
# test-phase7.ps1 keeps passing. Which means alice alone cannot prove which source answered.
$r = Call "$roleUrl/1?tenant=acme" $mgmtToken
if ($r.Content.Trim() -ne "Admin") { throw "Expected Admin for alice at acme, got: $($r.Content)" }
# carol proves it. She is a federated identity with NO row in UserIdentityRoles at all, so before this
# phase she got the "Member" fallback. "Underwriter" exists nowhere in this repo's databases - only in
# Acme's own directory.
$r = Call "$roleUrl/$([uri]::EscapeDataString($carolLocalId))?tenant=acme" $mgmtToken
if ($r.Content.Trim() -ne "Underwriter") {
    throw "Expected Underwriter for carol from Acme's own API, got: $($r.Content)"
}
# And the same subject with NO tenant named takes the pre-Phase-12 path: no connector lookup, no HTTP
# call to :5014, straight to the local table and its fallback.
$r = Call "$roleUrl/$([uri]::EscapeDataString($carolLocalId))" $mgmtToken
if ($r.Content.Trim() -ne "Member") { throw "With no tenant, carol should fall back to Member, got: $($r.Content)" }
Write-Host "   PASS - carol=Underwriter with ?tenant=acme, Member without it (same subject, two sources)" -ForegroundColor Green

Write-Host "3. The cascade really is ORDERED - position 2 answers only when position 1 doesn't..." -ForegroundColor Cyan
# The claim connector reads a claim named by a database row off the CALLER's token. No real caller of
# this endpoint carries a "role" claim (the IdG's self-issued JWT has iss/nbf/iat/exp/client_id/aud and
# nothing else), so on the login path position 2 correctly produces nothing - which leaves "reached it
# and found nothing" indistinguishable from "never got there."
#
# userservice-claimprobe-svc exists to separate those. It carries a "role" client claim with the
# prefix cleared, so the claim connector CAN answer for it.
$probeToken = GetClientCredentialsToken "userservice-claimprobe-svc" "userservice-claimprobe-secret" "userapi"
# bob is not in Acme's directory, so position 1 answers 404 -> no value -> the chain moves on, and
# position 2 reads the claim off this token.
$r = Call "$roleUrl/2?tenant=acme" $probeToken
if ($r.Content.Trim() -ne "UnderwriterFromClaim") {
    throw "Expected the Claim connector (position 2) to answer for bob, got: $($r.Content)"
}
# alice IS in Acme's directory, so position 1 answers and position 2 is never consulted - with the
# very same token that just proved it can answer. That is the ordering, demonstrated rather than
# asserted from configuration.
$r = Call "$roleUrl/1?tenant=acme" $probeToken
if ($r.Content.Trim() -ne "Admin") {
    throw "Position 1 should have answered for alice, shadowing the claim; got: $($r.Content)"
}
Write-Host "   PASS - bob=UnderwriterFromClaim (position 2), alice=Admin (position 1 wins)" -ForegroundColor Green

Write-Host "4. globex opted in and is STILL served by the old path - both IsEnabled flags must be true..." -ForegroundColor Cyan
# globex has an enabled ConnectorHandlerTenants row picking AzureGraph. The base ConnectorHandlers
# pairing for AzureGraph x GetUserRole is disabled, so resolution finds nothing, warns, and returns
# null - and the endpoint falls back to UserIdentityRoles. Exactly the Phase 11 behaviour.
$r = Call "$roleUrl/1?tenant=globex" $mgmtToken
if ($r.Content.Trim() -ne "Admin") { throw "globex/alice should still come from the local table (Admin), got: $($r.Content)" }
$r = Call "$roleUrl/2?tenant=globex" $mgmtToken
if ($r.Content.Trim() -ne "Member") { throw "globex/bob should still fall back to Member, got: $($r.Content)" }
# The control that makes the above mean something: globex's answer does NOT depend on Acme's API. Ask
# with the claim-probe token, which position 2 of acme's chain happily answers, and globex still reads
# the table - because globex resolves no connector at all.
$r = Call "$roleUrl/2?tenant=globex" $probeToken
if ($r.Content.Trim() -ne "Member") { throw "globex should resolve NO connector, got: $($r.Content)" }
Write-Host "   PASS - a tenant can be fully configured on paper and resolve to no connector" -ForegroundColor Green

Write-Host "5. The same misconfiguration is INVISIBLE through a cascade and a 502 without one..." -ForegroundColor Cyan
# initech's WebApiConnectorConfiguration.Host names Acme's API - the single most likely connector
# misconfiguration there is. Its role chain absorbs the 403 and falls through to a claim that isn't
# there, so every initech user quietly becomes "Member". Nothing fails. No login breaks.
$r = Call "$roleUrl/1?tenant=initech" $mgmtToken
if ($r.StatusCode -ne 200) { throw "initech's role lookup should still succeed, got $($r.StatusCode)" }
if ($r.Content.Trim() -ne "Member") { throw "initech should absorb the 403 and answer Member, got: $($r.Content)" }
# The email lookup is the SAME tenant, the SAME host, the SAME 403 - and it is not cascading, so
# nothing absorbs it and the caller finally sees the fault.
$r = Call "${emailUrl}?email=alice%40acme.test&tenant=initech" $mgmtToken
if ($r.StatusCode -ne 502) { throw "initech's non-cascading email lookup should 502, got $($r.StatusCode): $($r.Content)" }
if ($r.Content -notmatch "403") { throw "The 502 should name the upstream status; body was: $($r.Content)" }
Write-Host "   PASS - role 200/Member (absorbed), email 502 naming the upstream 403 (surfaced)" -ForegroundColor Green

Write-Host "6. The non-cascading handler tells 'no such user' apart from 'no source' and 'broken'..." -ForegroundColor Cyan
$r = Call "${emailUrl}?email=alice%40acme.test&tenant=acme" $mgmtToken
if ($r.StatusCode -ne 200) { throw "acme's email lookup should 200, got $($r.StatusCode): $($r.Content)" }
$found = $r.Content | ConvertFrom-Json
if ($found.userId -ne "1" -or $found.role -ne "Admin") { throw "Unexpected body from the email lookup: $($r.Content)" }
# 404: Acme's own API answered "no such user". A 404 from a tenant's system is an ANSWER, and turning
# it into a 502 was a real bug in this phase's first implementation - see the README.
$r = Call "${emailUrl}?email=nobody%40acme.test&tenant=acme" $mgmtToken
if ($r.StatusCode -ne 404) { throw "An unknown email at acme should 404, got $($r.StatusCode): $($r.Content)" }
# 501: nobody was asked. globex has no enabled connector for this handler and there is no local table
# of email addresses to fall back to, so "no such user" would be a lie.
$r = Call "${emailUrl}?email=alice%40acme.test&tenant=globex" $mgmtToken
if ($r.StatusCode -ne 501) { throw "globex has no email connector; expected 501, got $($r.StatusCode)" }
# 400 and 404 for the two ways of not naming a usable tenant.
$r = Call "${emailUrl}?email=alice%40acme.test" $mgmtToken
if ($r.StatusCode -ne 400) { throw "An email lookup with no tenant should 400, got $($r.StatusCode)" }
$r = Call "${emailUrl}?email=alice%40acme.test&tenant=nosuchtenant" $mgmtToken
if ($r.StatusCode -ne 404) { throw "An unknown tenant should 404, got $($r.StatusCode)" }
Write-Host "   PASS - 200 hit, 404 no such user, 501 no source, 400 no tenant, 404 unknown tenant" -ForegroundColor Green

Write-Host "7. It reaches a real login: a token's role claim now comes out of Acme's own system..." -ForegroundColor Cyan
# The end-to-end point of the phase. IdentityServerHost's SampleProfileService passes tenant_id along
# to Mini.UserService, which resolves acme's connector chain, which calls :5014.
$aliceClaims = LoginAndGetClaims "alice" "alice" "acme"
if ($aliceClaims.role -ne "Admin") { throw "Expected role=Admin on alice's token, got: $($aliceClaims.role)" }
if ($aliceClaims.tenant_id -ne "acme") { throw "Expected tenant_id=acme, got: $($aliceClaims.tenant_id)" }
# bob at globex still gets his role from the table, in the same run, from the same host.
$bobClaims = LoginAndGetClaims "bob" "bob" "globex"
if ($bobClaims.role -ne "Member") { throw "Expected role=Member on bob's token, got: $($bobClaims.role)" }
Write-Host "   PASS - alice's role via Acme's API, bob's via the local table, one IdG, no if-statements" -ForegroundColor Green

Write-Host "8. Deactivating a tenant takes its connectors out of service too..." -ForegroundColor Cyan
# Connector configuration is keyed on the tenant GUID, and the endpoint resolves key -> GUID through
# the Tenants table with an IsActive filter. So there is no side door: a deactivated tenant cannot
# resolve a connector even though its connector rows are untouched.
$newKey = "phase12-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmss")
$r = Call "https://localhost:5013/api/v1/management/tenants" $mgmtToken "POST" `
    "{""key"":""$newKey"",""name"":""Phase 12 Test Tenant"",""description"":""created by test-phase12.ps1""}"
if ($r.StatusCode -ne 201) { throw "Expected 201 creating a tenant, got $($r.StatusCode): $($r.Content)" }
# A brand-new tenant has no connector rows at all, so the role lookup falls back to the local table -
# which is what makes onboarding safe: a tenant exists before any integration is configured for it.
$r = Call "$roleUrl/1?tenant=$newKey" $mgmtToken
if ($r.Content.Trim() -ne "Admin") { throw "A connector-less new tenant should read the local table, got: $($r.Content)" }
$r = Call "https://localhost:5013/api/v1/management/tenants/$newKey" $mgmtToken "DELETE"
if ($r.StatusCode -ne 204) { throw "Deleting the tenant should 204, got $($r.StatusCode)" }
$r = Call "$roleUrl/1?tenant=$newKey" $mgmtToken
if ($r.StatusCode -ne 404) { throw "A deleted tenant should 404 rather than fall back, got $($r.StatusCode)" }
Write-Host "   PASS - created '$newKey', local-table fallback, deleted, then 404 (not a silent fallback)" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 12 CONNECTORS: PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Now run test-phase7.ps1. It is unmodified since Phase 7 and asserts alice's role is" -ForegroundColor DarkGray
Write-Host "'Admin' - which is now Acme's own web API answering, not a row in MiniUsers. Holding the" -ForegroundColor DarkGray
Write-Host "value constant while the source moves is what makes that provable. test-phase3/4/9/10/11" -ForegroundColor DarkGray
Write-Host "and 'dotnet test' are the rest of the regression suite." -ForegroundColor DarkGray
Write-Host ""
Write-Host "Not scripted (needs stopping a process): what a tenant's own system going down costs." -ForegroundColor Yellow
Write-Host "Stop Mini.AcmeApi:  Get-Process dotnet | ... (or just kill the :5014 process)" -ForegroundColor Yellow
Write-Host "Then log in as alice at tenant:acme. The login SUCCEEDS and her role is 'Member' - the" -ForegroundColor Yellow
Write-Host "cascade absorbed a connection failure exactly as it absorbs Initech's 403, and a privilege" -ForegroundColor Yellow
Write-Host "quietly disappeared from her token. Now run test-phase7.ps1 and watch it fail on the role" -ForegroundColor Yellow
Write-Host "assertion, pointing at IdentityServerHost, which is not the process that is broken." -ForegroundColor Yellow
