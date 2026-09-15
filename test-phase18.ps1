# Verifies Phase 18: Agent Portal calls Mini.AuthorizationService via Mini.Infrastructure's shared,
# resilient AuthorizationClient (extracted in Phase 16, previously only consumed by SampleApi). Raw HTTP,
# no browser — reuses the NewClient/Follow/PKCE-shaped, PAR + form_post login helpers from
# test-phase17.ps1 (AgentPortal's OIDC flow is PAR-shaped, unlike MvcClient's, which disables PAR — see
# AgentPortal/README.md's Phase 17 "Things that broke" section), and the .authorized/.reason assertion
# style test-phase14.ps1 uses for SampleApi's own authorization integration — adapted here to parse the
# rendered AuthorizationResult.cshtml view instead of a JSON body, because Phase 18 deliberately renders a
# VIEW (mirroring MvcClient's CallApi pattern), not a JSON endpoint (see README.md's Phase 18 section for
# why).
#
# Run IdentityServerHost, Mini.AuthorizationService and AgentPortal first (or `.\run-all.ps1`), then run
# this script.

$ErrorActionPreference = "Stop"

function NewClient() {
    $cookies = New-Object System.Net.CookieContainer
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $handler.CookieContainer = $cookies
    return New-Object System.Net.Http.HttpClient($handler)
}

function Follow($client, $uri, $method = "GET", $formFields = $null) {
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
        if ($status -lt 300 -or $status -ge 400) { return @{ StatusCode = $status; Content = $content; Uri = $uri; Response = $response } }
        $location = $response.Headers.Location
        $uri = if ($location.IsAbsoluteUri) { $location.ToString() } else { [System.Uri]::new([System.Uri]$uri, $location).ToString() }
        $method = "GET"; $formFields = $null
    }
    throw "Too many redirects, stopped at $uri"
}

# Drives a full AgentPortal login for the given local test user, returning the still-cookie-bearing
# $client so the caller can make further authenticated requests against AgentPortal with it.
function LoginToAgentPortal($username, $password) {
    $client = NewClient
    $resp = Follow $client "https://localhost:5016/Home/Secure"
    if ($resp.Content -notmatch "Sign in") { throw "Expected the IdentityServerHost login page, got: $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))" }

    $returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
    $verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
    $body = @{ Username = $username; Password = $password; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
    $resp = Follow $client "https://localhost:5001/Account/Login" "POST" $body

    # The authorize callback comes back as an auto-posting HTML form (response_mode=form_post) — Follow()
    # only follows 3xx redirects, so the form's POST has to be driven by hand, same as test-phase17.ps1.
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

# Parses the two fields AuthorizationResult.cshtml renders out of a plain HTML table — the view
# equivalent of test-phase14.ps1's "$authz = $resp.Content | ConvertFrom-Json" against a JSON body.
function ParseAuthorizationResult($html) {
    $authorized = [regex]::Match($html, "<td><strong>Authorized</strong></td><td>(\w+)</td>").Groups[1].Value
    $reason = [regex]::Match($html, "<td><strong>Reason</strong></td><td>([^<]*)</td>").Groups[1].Value
    if (-not $authorized) { throw "Could not find an Authorized field in the response: $($html.Substring(0, [Math]::Min(400, $html.Length)))" }
    return @{ Authorized = ($authorized -eq "True"); Reason = $reason }
}

Write-Host ""
Write-Host "Phase 18: Agent Portal calls Mini.AuthorizationService via the shared client" -ForegroundColor Cyan

Write-Host ""
Write-Host "1. AgentPortal login still works (regression from Phase 17, now requesting api1+tenant too)..." -ForegroundColor Yellow
$aliceClient = LoginToAgentPortal "alice" "alice"
Write-Host "   PASS - alice reached AgentPortal's own secure page" -ForegroundColor Green

Write-Host ""
Write-Host "2. A signed-in Acme user (alice, role Admin) can check authorization for 'agent-portal' and gets a REAL decision..." -ForegroundColor Yellow
$resp = Follow $aliceClient "https://localhost:5016/Home/CheckAuthorization"
if ($resp.StatusCode -ne 200) { throw "Expected 200 from CheckAuthorization for alice, got $($resp.StatusCode): $($resp.Content)" }
$authz = ParseAuthorizationResult $resp.Content
if ($authz.Authorized -ne $true) { throw "Expected alice (acme, Admin) to be authorized for 'agent-portal' (Acme's policy requires Admin), got: $($authz | ConvertTo-Json)" }
Write-Host "   PASS - alice (acme) authorized=$($authz.Authorized), reason: $($authz.Reason)" -ForegroundColor Green

Write-Host ""
Write-Host "3. A signed-in Globex user (bob, role Member) gets a per-tenant decision from the SAME resource..." -ForegroundColor Yellow
$bobClient = LoginToAgentPortal "bob" "bob"
$resp = Follow $bobClient "https://localhost:5016/Home/CheckAuthorization"
if ($resp.StatusCode -ne 200) { throw "Expected 200 from CheckAuthorization for bob, got $($resp.StatusCode): $($resp.Content)" }
$authz = ParseAuthorizationResult $resp.Content
if ($authz.Authorized -ne $true) { throw "Expected bob (globex, Member) to be authorized for 'agent-portal' (Globex's policy requires Member), got: $($authz | ConvertTo-Json)" }
Write-Host "   PASS - bob (globex) authorized=$($authz.Authorized), reason: $($authz.Reason)" -ForegroundColor Green

Write-Host ""
Write-Host "4. Unauthenticated access to CheckAuthorization is rejected (challenged, not shown the result)..." -ForegroundColor Yellow
$anonClient = NewClient
$resp = Follow $anonClient "https://localhost:5016/Home/CheckAuthorization"
# Same shape as every other [Authorize]-gated action in this repo's MVC clients: no session means the
# cookie scheme finds nothing, "oidc" challenges, and the browser (or here, our raw HTTP client) ends up
# redirected all the way to IdentityServerHost's own login page — never AgentPortal's authorization result.
if ($resp.Uri -notmatch "localhost:5001" -or $resp.Content -notmatch "Sign in") {
    throw "Expected an anonymous CheckAuthorization request to be challenged to IdentityServerHost's login page, landed on $($resp.Uri): $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))"
}
Write-Host "   PASS - anonymous request never saw an authorization result, challenged to $($resp.Uri.Split('?')[0]) instead" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 18 AGENT PORTAL AUTHORIZATION: PASS" -ForegroundColor Green
