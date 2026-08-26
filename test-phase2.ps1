# Walks the full Authorization Code + PKCE flow via HTTP, no browser required.
# Run both projects first (dotnet run in src/IdentityServerHost and src/MvcClient), then run this script.
#
# Uses raw HttpClient + CookieContainer with AllowAutoRedirect disabled, walking redirects one hop at a
# time ourselves. Invoke-WebRequest's built-in redirect-following silently dropped cookies set on
# intermediate hops in testing (the MVC client's OIDC correlation cookie, set on the very first 302, never
# made it into the session) - this gives full control over exactly what gets sent where.

$ErrorActionPreference = "Stop"

$cookies = New-Object System.Net.CookieContainer
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = $cookies
$client = New-Object System.Net.Http.HttpClient($handler)

function ToFormContent($fields) {
    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string, string]]]::new()
    foreach ($k in $fields.Keys) { $pairs.Add([System.Collections.Generic.KeyValuePair[string, string]]::new($k, $fields[$k])) }
    return [System.Net.Http.FormUrlEncodedContent]::new($pairs)
}

function Follow($uri, $method = "GET", $formFields = $null) {
    for ($i = 0; $i -lt 10; $i++) {
        $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
        if ($formFields) { $request.Content = ToFormContent $formFields }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($status -lt 300 -or $status -ge 400) { return @{ StatusCode = $status; Content = $content } }
        $location = $response.Headers.Location
        $uri = if ($location.IsAbsoluteUri) { $location.ToString() } else { [System.Uri]::new([System.Uri]$uri, $location).ToString() }
        $method = "GET"; $formFields = $null   # redirects are always followed as GET, per HTTP convention
    }
    throw "Too many redirects, stopped at $uri"
}

Write-Host "1. GET the protected page while logged out..."
$resp = Follow "http://localhost:5002/Home/Secure"
if ($resp.Content -notmatch "Sign in") { throw "Expected to land on the login page, got something else: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Landed on the IdentityServer login page as expected." -ForegroundColor Green

$returnUrlRaw = [regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value
$returnUrl = [System.Net.WebUtility]::HtmlDecode($returnUrlRaw)  # browsers decode HTML entities (&amp; -> &) before submitting a form; we must too
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
if (-not $returnUrl -or -not $verToken) { throw "Could not parse ReturnUrl or antiforgery token from login page" }

Write-Host "2. POST credentials (alice/alice) to the login form..."
$body = @{
    Username = "alice"
    Password = "alice"
    ReturnUrl = $returnUrl
    __RequestVerificationToken = $verToken
}
$resp = Follow "http://localhost:5000/Account/Login" "POST" $body

Write-Host "3. Complete the response_mode=form_post hop (no browser JS here, so submit it by hand)..."
$formAction = [regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value
$formAction = [System.Net.WebUtility]::HtmlDecode($formAction)
if (-not $formAction) { throw "Expected an auto-post form (response_mode=form_post) from the authorize callback, got something else: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }

$hiddenFields = @{}
[regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object {
    $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value)
}

$resp = Follow $formAction "POST" $hiddenFields

Write-Host "4. Confirm we landed back on the secure page, authenticated..."
if ($resp.Content -notmatch "You're signed in") { throw "Did not land on the authenticated secure page: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Landed on Home/Secure, authenticated." -ForegroundColor Green

if ($resp.Content -notmatch "Alice Anderson") { throw "Expected 'name' claim (Alice Anderson) missing from secure page" }
Write-Host "   'name' claim (Alice Anderson) present in the rendered claims table." -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 2 END-TO-END FLOW: PASS" -ForegroundColor Green
