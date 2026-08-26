# Walks the same login flow as test-phase2.ps1, then hits MvcClient's "Call the API" action and checks
# that SampleApi actually answered with the signed-in user's claims.
# Run all three projects first (dotnet run in IdentityServerHost, MvcClient, and SampleApi), then run this.

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

Write-Host "1. Log in as alice (same flow as test-phase2.ps1)..."
$resp = Follow "http://localhost:5002/Home/Secure"
$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
if (-not $returnUrl -or -not $verToken) { throw "Could not parse ReturnUrl or antiforgery token from login page" }
$resp = Follow "http://localhost:5000/Account/Login" "POST" @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken }
$formAction = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, "action=['""]([^'""]*)['""]").Groups[1].Value)
if (-not $formAction) { throw "Expected an auto-post form (response_mode=form_post) from the authorize callback" }
$hiddenFields = @{}
[regex]::Matches($resp.Content, "name=['""]([^'""]+)['""] value=['""]([^'""]*)['""]") | ForEach-Object { $hiddenFields[$_.Groups[1].Value] = [System.Net.WebUtility]::HtmlDecode($_.Groups[2].Value) }
$resp = Follow $formAction "POST" $hiddenFields
if ($resp.Content -notmatch "You're signed in") { throw "Login did not complete: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   Logged in." -ForegroundColor Green

Write-Host "2. Click 'Call the API' (GET /Home/CallApi)..."
$resp = Follow "http://localhost:5002/Home/CallApi"
if ($resp.Content -notmatch "HTTP 200") { throw "Expected the API call to succeed with HTTP 200: $($resp.Content.Substring(0, [Math]::Min(300, $resp.Content.Length)))" }
Write-Host "   SampleApi answered with HTTP 200." -ForegroundColor Green

if ($resp.Content -notmatch "api1") { throw "Expected the 'api1' scope/audience claim to be present in SampleApi's response" }
Write-Host "   'api1' scope/audience present in the access token SampleApi validated." -ForegroundColor Green

if ($resp.Content -notmatch "Alice Anderson") { throw "Expected the 'name' claim to reach SampleApi via the access token" }
Write-Host "   'name' claim (Alice Anderson) reached SampleApi via the access token." -ForegroundColor Green

Write-Host ""
Write-Host "API CALL FROM MVCCLIENT: PASS" -ForegroundColor Green
