# Verifies SampleApi's port of Services.Authorization's identity/tenant plumbing (see
# src/SampleApi/docs/identity-context-and-conventions.md):
#   - IIdentityContext resolves IdentityType + TenantKey differently for a user token (tenant_id claim)
#     vs. a service-account token (parsed from the client_id suffix).
#   - The versioned route (/api/v1/identity) is live.
#   - ServiceAccountOnlyFilter on DELETE /api/v1/admin/cache/{tenantKey} accepts service callers only:
#     401 with no token, 403 for a real user's token, 200 for a service-account token.
#   - AddProblemDetails() turns the 401 into an application/problem+json body, not an empty one.
# Run IdentityServerHost and SampleApi first (dotnet run in each), then run this script.

$ErrorActionPreference = "Stop"

function Base64UrlEncode([byte[]]$bytes) {
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$cookies = New-Object System.Net.CookieContainer
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = $cookies
$client = New-Object System.Net.Http.HttpClient($handler)

function Follow($uri, $method = "GET", $formFields = $null, $stopAtHost = $null) {
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
        if ($status -lt 300 -or $status -ge 400) { return @{ StatusCode = $status; Content = $content; Uri = $uri } }
        $location = $response.Headers.Location
        $uri = if ($location.IsAbsoluteUri) { $location.ToString() } else { [System.Uri]::new([System.Uri]$uri, $location).ToString() }
        if ($stopAtHost -and $uri -like "*$stopAtHost*") { return @{ StatusCode = $status; Content = $content; Uri = $uri } }
        $method = "GET"; $formFields = $null
    }
    throw "Too many redirects, stopped at $uri"
}

Write-Host "1. Log in as alice via reactspa, requesting 'openid profile api1 tenant' with acr_values=tenant:acme..."
$verifierBytes = [byte[]]::new(32)
[System.Security.Cryptography.RandomNumberGenerator]::Fill($verifierBytes)
$codeVerifier = Base64UrlEncode $verifierBytes
$challengeBytes = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::ASCII.GetBytes($codeVerifier))
$codeChallenge = Base64UrlEncode $challengeBytes

$authorizeUrl = "http://localhost:5000/connect/authorize?client_id=reactspa&redirect_uri=" + `
    [uri]::EscapeDataString("http://localhost:5173/callback") + `
    "&response_type=code&response_mode=query&scope=" + [uri]::EscapeDataString("openid profile api1 tenant") + `
    "&acr_values=" + [uri]::EscapeDataString("tenant:acme") + `
    "&code_challenge=$codeChallenge&code_challenge_method=S256&state=teststate123"
$resp = Follow $authorizeUrl
$returnUrl = [System.Net.WebUtility]::HtmlDecode([regex]::Match($resp.Content, 'name="ReturnUrl" value="([^"]*)"').Groups[1].Value)
$verToken  = [regex]::Match($resp.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]*)"').Groups[1].Value
$resp = Follow "http://localhost:5000/Account/Login" "POST" @{ Username = "alice"; Password = "alice"; ReturnUrl = $returnUrl; __RequestVerificationToken = $verToken } "localhost:5173"
if ($resp.Uri -notmatch "code=") { throw "Expected the final redirect to carry ?code=..., landed on: $($resp.Uri)" }
$code = [System.Web.HttpUtility]::ParseQueryString(([uri]$resp.Uri).Query)["code"]

$resp = Follow "http://localhost:5000/connect/token" "POST" @{
    grant_type = "authorization_code"; code = $code; redirect_uri = "http://localhost:5173/callback"
    client_id = "reactspa"; code_verifier = $codeVerifier
}
if ($resp.StatusCode -ne 200) { throw "Token endpoint rejected the request: $($resp.Content)" }
$userToken = ($resp.Content | ConvertFrom-Json).access_token
Write-Host "   Got alice's access token." -ForegroundColor Green

Write-Host "2. GET /api/v1/identity with alice's token..."
$req = [System.Net.Http.HttpRequestMessage]::new("GET", "http://localhost:5003/api/v1/identity")
$req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $userToken)
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
$body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
if ([int]$resp.StatusCode -ne 200) { throw "Expected HTTP 200, got $([int]$resp.StatusCode): $body" }
if ($body.identity.identityType -ne "User") { throw "Expected identityType=User, got: $($body.identity.identityType)" }
if ($body.identity.tenantKey -ne "acme") { throw "Expected tenantKey=acme (from tenant_id claim), got: $($body.identity.tenantKey)" }
if (-not $body.identity.subject) { throw "Expected a non-null subject for a user token" }
Write-Host "   PASS - IdentityType=User, TenantKey=acme (resolved from the 'tenant_id' claim)." -ForegroundColor Green

Write-Host "3. Get a service-account token for mvcclient-svc.acme (client_credentials, no user at all)..."
$resp = Follow "http://localhost:5000/connect/token" "POST" @{
    grant_type = "client_credentials"; client_id = "mvcclient-svc.acme"; client_secret = "acme-svc-secret"; scope = "api1"
}
if ($resp.StatusCode -ne 200) { throw "Token endpoint rejected the service account: $($resp.Content)" }
$svcToken = ($resp.Content | ConvertFrom-Json).access_token

Write-Host "4. GET /api/v1/identity with the service-account token..."
$req = [System.Net.Http.HttpRequestMessage]::new("GET", "http://localhost:5003/api/v1/identity")
$req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $svcToken)
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
$body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
if ([int]$resp.StatusCode -ne 200) { throw "Expected HTTP 200, got $([int]$resp.StatusCode): $body" }
if ($body.identity.identityType -ne "Service") { throw "Expected identityType=Service, got: $($body.identity.identityType)" }
if ($body.identity.subject) { throw "Expected a null subject for a client-credentials token, got: $($body.identity.subject)" }
if ($body.identity.clientId -ne "mvcclient-svc.acme") { throw "Expected clientId=mvcclient-svc.acme, got: $($body.identity.clientId)" }
if ($body.identity.tenantKey -ne "acme") { throw "Expected tenantKey=acme (parsed from client_id suffix), got: $($body.identity.tenantKey)" }
Write-Host "   PASS - IdentityType=Service, TenantKey=acme (parsed from client_id, no tenant_id claim exists)." -ForegroundColor Green

Write-Host "5. DELETE /api/v1/admin/cache/acme with NO token (ServiceAccountOnlyFilter should 401)..."
$req = [System.Net.Http.HttpRequestMessage]::new("DELETE", "http://localhost:5003/api/v1/admin/cache/acme")
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
if ([int]$resp.StatusCode -ne 401) { throw "Expected 401, got $([int]$resp.StatusCode)" }
$contentType = $resp.Content.Headers.ContentType.MediaType
if ($contentType -ne "application/problem+json") { throw "Expected application/problem+json content-type, got: $contentType" }
$problemBody = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
if ($problemBody.status -ne 401) { throw "Expected problem details 'status' field = 401, got: $($problemBody.status)" }
Write-Host "   PASS - 401, application/problem+json body (AddProblemDetails() at work)." -ForegroundColor Green

Write-Host "6. DELETE /api/v1/admin/cache/acme with alice's USER token (ServiceAccountOnlyFilter should 403)..."
$req = [System.Net.Http.HttpRequestMessage]::new("DELETE", "http://localhost:5003/api/v1/admin/cache/acme")
$req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $userToken)
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
if ([int]$resp.StatusCode -ne 403) { throw "Expected 403, got $([int]$resp.StatusCode)" }
Write-Host "   PASS - a real user's own token is forbidden from a service-account-only endpoint." -ForegroundColor Green

Write-Host "7. DELETE /api/v1/admin/cache/acme with the SERVICE-ACCOUNT token (ServiceAccountOnlyFilter should 200)..."
$req = [System.Net.Http.HttpRequestMessage]::new("DELETE", "http://localhost:5003/api/v1/admin/cache/acme")
$req.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $svcToken)
$resp = $client.SendAsync($req).GetAwaiter().GetResult()
$body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
if ([int]$resp.StatusCode -ne 200) { throw "Expected 200, got $([int]$resp.StatusCode): $body" }
if ($body -notmatch "acme") { throw "Expected the tenant key 'acme' echoed back, got: $body" }
Write-Host "   PASS - a service-account token is let through." -ForegroundColor Green

Write-Host ""
Write-Host "SAMPLEAPI IDENTITY CONTEXT + API CONVENTIONS: PASS" -ForegroundColor Green
