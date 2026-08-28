# Verifies the multi-tenancy + IdentityGatewayApi + ExternalServicesApi port (from Applications.Apply)
# end-to-end: logs in as alice on Acme, confirms MvcClient's own ITenantContext resolved a tenant from
# the "tenant_id" claim, then calls SampleApi twice — once forwarding alice's own token, once fetching a
# service-account (client-credentials) token via ITokenClient — and confirms the two responses carry
# meaningfully different claims (a real user vs. no user at all).
# Run ExternalIdp, IdentityServerHost, MvcClient, and SampleApi first, then run this script.

$ErrorActionPreference = "Stop"

$cookies = New-Object System.Net.CookieContainer
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = $cookies
$client = New-Object System.Net.Http.HttpClient($handler)

function Follow($uri, $method = "GET", $formFields = $null) {
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
        $method = "GET"; $formFields = $null
    }
    throw "Too many redirects, stopped at $uri"
}

Write-Host "1. Log in as alice on Acme via MvcClient..."
$resp = Follow "http://localhost:5002/Home/LoginAsTenant?tenant=acme"
$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
if (-not $returnUrl -or -not $verToken) { throw "Could not parse ReturnUrl or antiforgery token from login page" }
$body = @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
$resp = Follow "http://localhost:5000/Account/Login" "POST" $body
$formAction = [regex]::Match($resp.Content, "action=['""]([^'""]*)['""]")
if ($formAction.Success) {
    $action = [System.Net.WebUtility]::HtmlDecode($formAction.Groups[1].Value)
    $hiddenFields = @{}
    [regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object {
        $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value)
    }
    $resp = Follow $action "POST" $hiddenFields
}
if ($resp.Content -notmatch "You're signed in") { throw "Login failed: $($resp.Content.Substring(0,[Math]::Min(300,$resp.Content.Length)))" }
if ($resp.Content -notmatch "Acme Corp \(acme\)") { throw "Expected Secure page to show resolved tenant 'Acme Corp (acme)', got: $($resp.Content.Substring(0,[Math]::Min(600,$resp.Content.Length)))" }
Write-Host "   PASS - logged in, ITenantContext resolved to Acme Corp (acme) on the Secure page." -ForegroundColor Green

Write-Host ""
Write-Host "2. Call the API as ME (forwarded user token)..."
$resp = Follow "http://localhost:5002/Home/CallApi"
if ($resp.Content -notmatch "HTTP 200") { throw "Expected HTTP 200, got: $($resp.Content.Substring(0,[Math]::Min(400,$resp.Content.Length)))" }
if ($resp.Content -notmatch "signed-in user") { throw "Expected the 'forwarded user token' label" }
if ($resp.Content -notmatch "Alice Anderson") { throw "Expected Alice's name claim via the forwarded user token" }
Write-Host "   PASS - forwarded-user-token call succeeded, shows Alice Anderson." -ForegroundColor Green

Write-Host ""
Write-Host "3. Call the API as the SERVICE ACCOUNT (client-credentials token)..."
$resp = Follow "http://localhost:5002/Home/CallApiAsServiceAccount"
if ($resp.Content -notmatch "HTTP 200") { throw "Expected HTTP 200, got: $($resp.Content.Substring(0,[Math]::Min(600,$resp.Content.Length)))" }
if ($resp.Content -notmatch "service-account token for tenant") { throw "Expected the service-account token label" }
if ($resp.Content -notmatch "acme") { throw "Expected tenant 'acme' in the label" }
if ($resp.Content -match "Alice Anderson") { throw "Service-account token should NOT carry Alice's name claim (no user behind a client-credentials grant)" }
if ($resp.Content -notmatch "mvcclient-svc.acme") { throw "Expected client_id 'mvcclient-svc.acme' in the claims" }
Write-Host "   PASS - service-account call succeeded, client_id=mvcclient-svc.acme, no user claims present." -ForegroundColor Green

Write-Host ""
Write-Host "MULTITENANCY + IDENTITYGATEWAYAPI + EXTERNALSERVICESAPI: PASS" -ForegroundColor Green
