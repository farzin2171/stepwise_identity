# Proves Phase 17: AgentPortal is a second, independently-configured MVC client that can complete a full
# login against the SAME IdentityServerHost as MvcClient, using its own client registration
# ("agentportal", not "mvcclient"). Raw HTTP, no browser — reuses the NewClient/Follow/PKCE helper pattern
# from test-phase3.ps1/test-phase4.ps1.
# Run IdentityServerHost and AgentPortal first (or `.\run-all.ps1`), then run this script.

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

Write-Host "1. AgentPortal's public Index page is reachable with no login..."
$client = NewClient
$resp = Follow $client "https://localhost:5016/"
if ($resp.StatusCode -ne 200 -or $resp.Content -notmatch "Agent Portal") { throw "Expected the Agent Portal home page, got HTTP $($resp.StatusCode): $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))" }
Write-Host "   PASS" -ForegroundColor Green

Write-Host "2. Hitting /Home/Secure with no session challenges against IdentityServerHost (client_id=agentportal)..."
$client = NewClient
$resp = Follow $client "https://localhost:5016/Home/Secure"
# Unlike MvcClient (which disables PAR - see its README's Phase 3 gotcha), AgentPortal leaves
# PushedAuthorizationBehavior at the handler's default (UseIfAvailable). Duende's discovery document
# advertises pushed_authorization_request_endpoint, so the handler pushes the real authorize parameters
# to /connect/par on a back channel and the browser redirect only ever carries
# "request_uri=urn:...&client_id=agentportal" - NOT the classic fully-visible query string MvcClient's
# tests match against. Confirmed by actually running this script and seeing the assertion below fail
# against the classic shape - see README.md's "Things that broke" section.
if ($resp.Uri -notmatch "localhost:5001" -or $resp.Uri -notmatch "request_uri" -or $resp.Uri -notmatch "agentportal") {
    throw "Expected a PAR-shaped redirect to IdentityServerHost naming agentportal, landed on: $($resp.Uri)"
}
if ($resp.Content -notmatch "Sign in") { throw "Expected the IdentityServerHost login page, got: $($resp.Content.Substring(0, [Math]::Min(200, $resp.Content.Length)))" }
Write-Host "   PASS - challenged to $($resp.Uri.Split('?')[0]) as agentportal" -ForegroundColor Green

Write-Host "3. Logging in as alice/alice completes the full code+token exchange and reaches the secure page..."
$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
$body = @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
$resp = Follow $client "https://localhost:5001/Account/Login" "POST" $body

# The authorize callback comes back as an auto-posting HTML form (response_mode=form_post), same as
# MvcClient's own test-api.ps1 has to handle - Follow() only follows 3xx redirects, so the form's POST
# has to be driven by hand here too.
$formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value)
if (-not $formAction) { throw "Expected an auto-post form (response_mode=form_post) from the authorize callback, got: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
$hiddenFields = @{}
[regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object { $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value) }
$resp = Follow $client $formAction "POST" $hiddenFields

if ($resp.Uri -notmatch "localhost:5016" -or $resp.Content -notmatch "You're signed in") {
    throw "Expected to land back on AgentPortal's secure page signed in, landed on $($resp.Uri): $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))"
}
if ($resp.Content -notmatch "alice") { throw "Expected alice's claims (e.g. 'name') on AgentPortal's own secure page" }
Write-Host "   PASS - AgentPortal's own /Home/Secure rendered a signed-in page for alice" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 17 AGENT PORTAL SKELETON: PASS" -ForegroundColor Green
