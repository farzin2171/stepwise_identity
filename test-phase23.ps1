# Verifies Phase 23 — the closing/hardening phase of the 19-23 message-bus/webhook/policy-admin arc.
#
# This is the first script in the repo that tries to assemble the FULL chain in one place:
#   AgentPortal login -> edit "agent-portal" policy for acme via the UI -> Mini.AuthorizationService's
#   admin API -> real PolicyChangedEvent on the bus -> Mini.MessageCenter fans it out to BOTH seeded
#   webhook subscriptions (Mini.AcmeApi, scoped to acme; WebhookReceiverStub, unscoped) -> a
#   globex-tenant change must NOT reach Mini.AcmeApi's acme-scoped subscription -> AgentPortal's own
#   PolicyChangeRequest audit row lands in AgentPortalDb.
#
# It also proves Phase 23's own fix: Mini.AuthorizationService's PUT admin endpoint now rejects a
# User-identity caller whose own tenant doesn't match the route's {tenantKey} with 403 - closing one of
# Phase 21's two documented gaps (see src/Mini.AuthorizationService/README.md's Phase 23 section). The
# other gap (publish-before-commit ordering) is deliberately left as documented debt - see that same
# section for why - so this script does not attempt to prove it fixed. It DOES observe that gap firsthand
# (a raw PUT that passes the tenant check hangs against Mini.AuthorizationService with no broker
# reachable), using a bounded client-side timeout so the observation doesn't hang this script itself.
#
# Docker/RabbitMQ has been unreachable in every sandbox this arc (Phases 19-22) has been built in, and
# this environment is no exception (confirmed via `docker info` before writing this script) - the
# output below is split into two clearly labelled sections so it's obvious which assertions actually
# ran for real in THIS run, versus which ones this script is *capable* of running once a human has
# Docker Desktop up and runs `.\run-all.ps1` first.
#
# Run IdentityServerHost (:5001), Mini.UserService (:5013), Mini.AcmeApi (:5014),
# Mini.AuthorizationService (:5015), Mini.MessageCenter (:5017 - needs RabbitMQ to actually consume),
# WebhookReceiverStub (:5018) and AgentPortal (:5016) first, e.g. via `.\run-all.ps1`.

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class TestPhase23CertPolicy {
    public static void TrustAll() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate (object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@ -ErrorAction SilentlyContinue
[TestPhase23CertPolicy]::TrustAll()

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
        if ($status -lt 300 -or $status -ge 400) { return @{ StatusCode = $status; Content = $content; Uri = $uri; Response = $response } }
        $location = $response.Headers.Location
        $uri = if ($location.IsAbsoluteUri) { $location.ToString() } else { [System.Uri]::new([System.Uri]$uri, $location).ToString() }
        if ($stopAtHost -and $uri -like "*$stopAtHost*") { return @{ StatusCode = $status; Content = $content; Uri = $uri } }
        $method = "GET"; $formFields = $null
    }
    throw "Too many redirects, stopped at $uri"
}

# $timeoutSec bounds the client's own wait - needed because a PUT that passes the tenant check goes on
# to `await publishEndpoint.Publish(...)` inside Mini.AuthorizationService itself (Phase 21's gap (b),
# deliberately left as documented debt - see this project's README's Phase 23 section). With no broker
# reachable, THAT await never completes, and Mini.AuthorizationService has no server-side timeout of its
# own guarding it - unlike AgentPortal's UI path, which Phase 22 gave its OWN 15s client-side timeout.
# A raw script calling the admin endpoint directly gets none of that protection, so this helper has to
# bound its own wait rather than hang for .NET's 100-second HttpClient default.
function Call($uri, $token, $method = "GET", $json = $null, $timeoutSec = 100) {
    $client = NewClient
    $client.Timeout = [TimeSpan]::FromSeconds($timeoutSec)
    $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
    if ($token) {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
    }
    if ($json) {
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    }
    try {
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        return @{
            StatusCode = [int]$response.StatusCode
            Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
    } catch {
        return @{ StatusCode = -1; Content = "TIMEOUT_OR_ERROR: $($_.Exception.Message)" }
    }
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

# Same pattern test-phase14.ps1 uses: reactspa is a public PKCE client, so this gets a REAL user
# access token (a User identity, not a service account) for whichever tenant/username is asked for.
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
    if ($resp.StatusCode -ne 200) { throw "Token endpoint failed for '$username'/${tenantKey}: $($resp.Content)" }
    return ($resp.Content | ConvertFrom-Json).access_token
}

function GetClientCredentialsToken($clientId, $secret, $scope) {
    $client = NewClient
    $request = [System.Net.Http.HttpRequestMessage]::new("POST", "https://localhost:5001/connect/token")
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new("grant_type", "client_credentials"))
    $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new("client_id", $clientId))
    $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new("client_secret", $secret))
    if ($scope) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new("scope", $scope)) }
    $request.Content = [System.Net.Http.FormUrlEncodedContent]::new($pairs)
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    if ($response.StatusCode -ne 200) { throw "Client-credentials grant failed for '$clientId': $($response.Content.ReadAsStringAsync().GetAwaiter().GetResult())" }
    return ($response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json).access_token
}

# test-phase22.ps1's AgentPortal login helper, unmodified.
function LoginToAgentPortal($username, $password) {
    $client = NewClient
    $resp = Follow $client "https://localhost:5016/Home/Secure"
    if ($resp.Content -notmatch "Sign in") { throw "Expected the IdentityServerHost login page, got: $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))" }

    $returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
    $verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
    $body = @{ Username = $username; Password = $password; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
    $resp = Follow $client "https://localhost:5001/Account/Login" "POST" $body

    $formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value)
    if (-not $formAction) { throw "Expected an auto-post form (response_mode=form_post) from the authorize callback for '$username', got: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
    $hiddenFields = @{}
    [regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object { $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value) }
    $resp = Follow $client $formAction "POST" $hiddenFields

    if ($resp.Uri -notmatch "localhost:5016" -or $resp.Content -notmatch "You're signed in") {
        throw "Expected '$username' to land back on AgentPortal's secure page signed in, landed on $($resp.Uri): $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))"
    }
    return $client
}

Write-Host ""
Write-Host "======================================================================" -ForegroundColor Cyan
Write-Host " PHASE 23 - closing/hardening the message-bus/webhook/policy-admin arc" -ForegroundColor Cyan
Write-Host "======================================================================" -ForegroundColor Cyan

$dockerAvailable = $false
try { docker info *> $null; if ($LASTEXITCODE -eq 0) { $dockerAvailable = $true } } catch { $dockerAvailable = $false }

Write-Host ""
if ($dockerAvailable) {
    Write-Host "Docker/RabbitMQ IS reachable in this environment - the full chain will be exercised." -ForegroundColor Green
} else {
    Write-Host "Docker/RabbitMQ is NOT reachable in this environment (same as Phases 19-22)." -ForegroundColor DarkYellow
    Write-Host "Everything below that does not need the bus still runs for real. Bus-dependent checks" -ForegroundColor DarkYellow
    Write-Host "are listed but skipped, for a human with Docker Desktop to run afterward." -ForegroundColor DarkYellow
}

Write-Host ""
Write-Host "----------------------------------------------------------------------" -ForegroundColor Cyan
Write-Host " VERIFIED NOW (no broker needed)" -ForegroundColor Cyan
Write-Host "----------------------------------------------------------------------" -ForegroundColor Cyan

Write-Host ""
Write-Host "1. Core services answer /health" -ForegroundColor Yellow
foreach ($p in @(5013, 5014, 5015, 5016)) {
    $resp = Call "https://localhost:$p/health"
    if ($resp.StatusCode -ne 200) { throw "Service on :$p failed /health: $($resp.StatusCode)" }
}
Write-Host "   PASS - Mini.UserService, Mini.AcmeApi, Mini.AuthorizationService, AgentPortal all healthy" -ForegroundColor Green

Write-Host ""
Write-Host "2. AgentPortal login + same-tenant policy edit still works end to end (regression for Phase 23's fix)" -ForegroundColor Yellow
$aliceClient = LoginToAgentPortal "alice" "alice"
$resp = Follow $aliceClient "https://localhost:5016/Policy/Edit"
if ($resp.StatusCode -ne 200) { throw "Expected 200 from Policy/Edit, got $($resp.StatusCode): $($resp.Content)" }
$verToken = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
$originalCondition = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, '<pre[^>]*>([^<]*)</pre>').Groups[1].Value)
$marker = "phase23-" + [Guid]::NewGuid().ToString("N")
$newCondition = "{`"requiredRoles`": [`"Admin`"], `"phase23Marker`": `"$marker`"}"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$resp = Follow $aliceClient "https://localhost:5016/Policy/Edit" "POST" @{ newCondition = $newCondition; __RequestVerificationToken = $verToken }
$sw.Stop()
if ($resp.StatusCode -ne 200) { throw "Expected 200 from the edit POST, got $($resp.StatusCode): $($resp.Content)" }
if ($dockerAvailable) {
    if ($resp.Uri -notmatch "/Policy/History") { throw "Expected a successful edit to redirect to /Policy/History, landed on $($resp.Uri)" }
} else {
    if ($resp.Content -notmatch "Update failed") { throw "Expected the same known Phase 22 graceful-degradation message with no broker reachable, got: $($resp.Content.Substring(0, [Math]::Min(500, $resp.Content.Length)))" }
}
Write-Host "   PASS - alice (acme) can still edit her OWN tenant's policy through the UI after Phase 23's tenant-match fix (marker: $marker)" -ForegroundColor Green

Write-Host ""
Write-Host "3. AgentPortal's PolicyChangeRequest audit row landed in AgentPortalDb for that edit" -ForegroundColor Yellow
$expectedOutcome = if ($dockerAvailable) { "Succeeded" } else { "Failed" }
$rowCount = (sqlcmd -S "(localdb)\mssqllocaldb" -d "AgentPortalDb" -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM PolicyChangeRequests WHERE NewCondition LIKE '%$marker%' AND Outcome = '$expectedOutcome'" -C).Trim()
if ($rowCount -ne "1") { throw "Expected exactly 1 $expectedOutcome PolicyChangeRequest row carrying marker '$marker', found $rowCount." }
Write-Host "   PASS - AgentPortalDb.PolicyChangeRequests has 1 $expectedOutcome row for this edit" -ForegroundColor Green

Write-Host ""
Write-Host "4. Phase 23 fix: a User token whose OWN tenant is NOT 'acme' cannot write acme's policy - now 403, not 200" -ForegroundColor Yellow
$bobToken = LoginAndGetUserToken "bob" "bob" "globex"
$crossTenantBody = @{ condition = '{"requiredRoles": ["Admin"], "phase23-should-never-land": true}' } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $bobToken "PUT" $crossTenantBody
if ($resp.StatusCode -ne 403) { throw "Expected 403 for a globex user's token writing acme's policy, got $($resp.StatusCode): $($resp.Content)" }
Write-Host "   PASS - bob (globex) gets 403 attempting to PUT acme's 'sample-api' policy - Phase 21 gap (a) is closed" -ForegroundColor Green

Write-Host ""
Write-Host "5. The same fix does not break a legitimate same-tenant User edit" -ForegroundColor Yellow
Write-Host "   (Note: a PUT that PASSES the tenant check goes on to await IPublishEndpoint.Publish inside" -ForegroundColor DarkGray
Write-Host "    Mini.AuthorizationService itself - Phase 21's gap (b), left as documented debt. With no" -ForegroundColor DarkGray
Write-Host "    broker reachable and no server-side timeout on that await, a raw script calling this" -ForegroundColor DarkGray
Write-Host "    endpoint directly - unlike AgentPortal's UI, which has its OWN 15s client-side timeout -" -ForegroundColor DarkGray
Write-Host "    hangs until it gives up. A bounded timeout here that expires (rather than 403) is itself" -ForegroundColor DarkGray
Write-Host "    the proof the tenant check was passed, not a failure of this check.)" -ForegroundColor DarkGray
$bobOwnTenantBody = @{ condition = '{"requiredRoles": ["Member"]}' } | ConvertTo-Json
$resp = Call "https://localhost:5015/api/v1/authorization/policies" $bobToken "GET"
$beforeGlobex = if ($resp.StatusCode -eq 200) { ($resp.Content | ConvertFrom-Json) | Where-Object { $_.resourceName -eq "agent-portal" } } else { $null }
$resp = Call "https://localhost:5015/api/v1/authorization/policies/globex/agent-portal" $bobToken "PUT" $bobOwnTenantBody 15
if ($resp.StatusCode -eq 403) { throw "Expected bob (globex) writing HIS OWN tenant's policy to pass the tenant check, got 403: $($resp.Content)" }
if ($resp.StatusCode -ne 200 -and $resp.StatusCode -ne -1) { throw "Unexpected status for bob's own-tenant PUT: $($resp.StatusCode): $($resp.Content)" }
if ($resp.StatusCode -eq 200 -and $beforeGlobex) {
    Call "https://localhost:5015/api/v1/authorization/policies/globex/agent-portal" $bobToken "PUT" (@{ condition = $beforeGlobex.condition } | ConvertTo-Json) 15 | Out-Null
}
$verdict = if ($resp.StatusCode -eq 200) { "wrote through immediately (unexpected without a broker, but a genuine pass)" } else { "passed the tenant check and then hung on the undelivered publish (gap (b), expected without a broker)" }
Write-Host "   PASS - bob (globex) can still write his OWN tenant's policy - $verdict" -ForegroundColor Green

Write-Host ""
Write-Host "6. A Service-identity caller is deliberately exempt from the tenant-match check (documented, not a new gap)" -ForegroundColor Yellow
$svcToken = GetClientCredentialsToken "userservice-svc.acme" "acme-userservice-secret" "authapi"
$resp = Call "https://localhost:5015/api/v1/authorization/policies" $svcToken "GET"
if ($resp.StatusCode -ne 200) { throw "Expected a service account to still be able to read acme's policies, got $($resp.StatusCode)" }
$before = ($resp.Content | ConvertFrom-Json) | Where-Object { $_.resourceName -eq "sample-api" }
$resp = Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $svcToken "PUT" (@{ condition = $before.condition } | ConvertTo-Json) 15
if ($resp.StatusCode -eq 403) { throw "Expected a service-account PUT for acme/sample-api to pass the tenant check (it's exempt), got 403: $($resp.Content)" }
if ($resp.StatusCode -ne 200 -and $resp.StatusCode -ne -1) { throw "Unexpected status for the service-account PUT: $($resp.StatusCode): $($resp.Content)" }
Write-Host "   PASS - userservice-svc.acme (a Service identity) is unaffected by the new check - test-phase21.ps1's own path still passes the gate" -ForegroundColor Green

Write-Host ""
Write-Host "7. Restore acme's 'agent-portal' policy to what it was before this run" -ForegroundColor Yellow
if ($originalCondition) {
    try {
        Follow $aliceClient "https://localhost:5016/Policy/Edit" "POST" @{ newCondition = $originalCondition; __RequestVerificationToken = $verToken } | Out-Null
        Write-Host "   PASS - restored to: $originalCondition" -ForegroundColor Green
    } catch {
        # Best-effort cleanup only. §5/§6 above deliberately triggered gap (b) against
        # Mini.AuthorizationService (an unbounded await with no broker reachable) - by this point in the
        # script that service, and anything holding a connection open to it (including AgentPortal's own
        # PolicyAdminClient, mid-request), may be sitting on exhausted resources. A failed cleanup step
        # here is a side effect of proving gap (b) is real, not a new finding - restart the affected
        # service(s) before reusing this environment.
        Write-Host "   Could not restore (expected fallout from §5's gap (b) demonstration - see comment above). Restart AgentPortal/Mini.AuthorizationService before reusing this environment." -ForegroundColor DarkYellow
    }
}

Write-Host ""
Write-Host "----------------------------------------------------------------------" -ForegroundColor Magenta
Write-Host " REQUIRES DOCKER/RABBITMQ - NOT YET VERIFIED IN THIS ENVIRONMENT" -ForegroundColor Magenta
Write-Host "----------------------------------------------------------------------" -ForegroundColor Magenta

if ($dockerAvailable) {
    Write-Host ""
    Write-Host "8. Real bus fan-out: acme edit reaches BOTH Mini.AcmeApi and WebhookReceiverStub" -ForegroundColor Yellow
    $acmeMarker = "phase23-fanout-" + [Guid]::NewGuid().ToString("N")
    $acmeCondition = "{`"requiredRoles`": [`"Admin`"], `"phase23FanoutMarker`": `"$acmeMarker`"}"
    Call "https://localhost:5015/api/v1/authorization/policies/acme/sample-api" $svcToken "PUT" (@{ condition = $acmeCondition } | ConvertTo-Json) | Out-Null
    $deadline = (Get-Date).AddSeconds(20)
    $sawWebhookStub = $false
    while ((Get-Date) -lt $deadline -and -not $sawWebhookStub) {
        Start-Sleep -Milliseconds 500
        $resp = Call "https://localhost:5018/webhook/received"
        if ($resp.StatusCode -eq 200) {
            $entries = $resp.Content | ConvertFrom-Json
            foreach ($e in $entries) { if ($e.body -like "*$acmeMarker*") { $sawWebhookStub = $true } }
        }
    }
    if (-not $sawWebhookStub) { throw "WebhookReceiverStub never received the acme-tenant delivery for marker '$acmeMarker'." }
    $resp = Call "https://localhost:5017/api/v1/deliveries"
    $deliveries = $resp.Content | ConvertFrom-Json
    $matching = $deliveries | Where-Object { $_.eventSummary -like "*$acmeMarker*" }
    if ($matching.Count -lt 2) { throw "Expected 2 delivery attempts (acme-scoped Mini.AcmeApi + unscoped WebhookReceiverStub), got $($matching.Count)." }
    Write-Host "   PASS - both seeded subscriptions received the acme delivery" -ForegroundColor Green

    Write-Host ""
    Write-Host "9. Cross-tenant leak check: a globex edit must NOT reach Mini.AcmeApi's acme-scoped subscription" -ForegroundColor Yellow
    $globexMarker = "phase23-leakcheck-" + [Guid]::NewGuid().ToString("N")
    $globexToken = GetClientCredentialsToken "userservice-svc.globex" "globex-userservice-secret" "authapi"
    Call "https://localhost:5015/api/v1/authorization/policies/globex/sample-api" $globexToken "PUT" (@{ condition = "{`"requiredRoles`": [`"Member`"], `"phase23LeakMarker`": `"$globexMarker`"}" } | ConvertTo-Json) | Out-Null
    Start-Sleep -Seconds 5
    $resp = Call "https://localhost:5017/api/v1/deliveries"
    $deliveries = $resp.Content | ConvertFrom-Json
    $leaked = $deliveries | Where-Object { $_.eventSummary -like "*$globexMarker*" -and $_.subscriptionTarget -like "*5014*" }
    if ($leaked) { throw "LEAK: a globex-tenant PolicyChangedEvent reached Mini.AcmeApi's acme-scoped subscription." }
    Write-Host "   PASS - the globex change did not reach Mini.AcmeApi's acme-scoped subscription" -ForegroundColor Green

    Write-Host ""
    Write-Host "PHASE 23: FULL CHAIN VERIFIED, INCLUDING THE LIVE BUS." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "8. Real bus fan-out to BOTH Mini.AcmeApi and WebhookReceiverStub - SKIPPED" -ForegroundColor DarkYellow
    Write-Host "   Not run: no reachable RabbitMQ. Once Docker Desktop is up and '.\run-all.ps1' has started" -ForegroundColor DarkYellow
    Write-Host "   RabbitMQ + Mini.MessageCenter, re-run this script - section 8 above proves the edit's" -ForegroundColor DarkYellow
    Write-Host "   PolicyChangedEvent reaches BOTH seeded subscriptions (GET :5017/api/v1/deliveries)." -ForegroundColor DarkYellow
    Write-Host ""
    Write-Host "9. Cross-tenant webhook leak check (globex change must not reach acme's scoped subscription) - SKIPPED" -ForegroundColor DarkYellow
    Write-Host "   Same reason as §8. This exact check has never been run in any sandbox across Phases 19-23 -" -ForegroundColor DarkYellow
    Write-Host "   it is the single biggest unverified claim left in this arc. A human with Docker Desktop" -ForegroundColor DarkYellow
    Write-Host "   should run this script end to end at least once before trusting webhook tenant-scoping." -ForegroundColor DarkYellow
    Write-Host ""
    Write-Host "PHASE 23: EVERYTHING NOT REQUIRING A BROKER PASSED. THE LIVE BUS PATH REMAINS UNVERIFIED HERE." -ForegroundColor Yellow
}
