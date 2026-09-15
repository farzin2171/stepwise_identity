# Verifies Phase 20: Mini.MessageCenter consumes PolicyChangedEvent off the message bus (Phase 19)
# and fans it out to webhook subscribers.
#
# LIMITATION, documented plainly: nothing in production code publishes PolicyChangedEvent yet — that
# is Phase 21's job (Mini.AuthorizationService gaining a policy-admin API). So this script triggers
# delivery through Mini.MessageCenter's own throwaway diagnostic endpoint,
# POST /api/v1/test/publish-policy-changed, the same way test-phase13.ps1 proved
# Mini.AuthorizationService worked before Phase 14 wired a real caller into it. That endpoint is
# documented in Mini.MessageCenter's Program.cs as removable once Phase 21 ships a real publisher.
#
# Needs a REAL RabbitMQ (Phase 19's dependency) — start everything with .\run-all.ps1 first, which
# starts RabbitMQ via docker-compose, then Mini.MessageCenter and WebhookReceiverStub. If Docker
# isn't available in your environment, this script cannot run — see the Phase 20 README section for
# what to verify by other means in that case.

$ErrorActionPreference = "Stop"

Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class TestPhase20CertPolicy {
    public static void TrustAll() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate (object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@ -ErrorAction SilentlyContinue
[TestPhase20CertPolicy]::TrustAll()

function Call($uri, $method = "GET", $json = $null) {
    $client = New-Object System.Net.Http.HttpClient
    $request = [System.Net.Http.HttpRequestMessage]::new($method, $uri)
    if ($json) {
        $request.Content = [System.Net.Http.StringContent]::new($json, [System.Text.Encoding]::UTF8, "application/json")
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    return @{
        StatusCode = [int]$response.StatusCode
        Content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
}

function ComputeHmac($json, $secret) {
    $hmac = New-Object System.Security.Cryptography.HMACSHA256([System.Text.Encoding]::UTF8.GetBytes($secret))
    $hash = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($json))
    return ($hash | ForEach-Object { $_.ToString("x2") }) -join ""
}

Write-Host ""
Write-Host "Phase 20: Mini.MessageCenter (webhook fan-out for PolicyChangedEvent)" -ForegroundColor Cyan

Write-Host ""
Write-Host "§1. Mini.MessageCenter and WebhookReceiverStub respond to /health" -ForegroundColor Yellow
$resp = Call "https://localhost:5017/health"
if ($resp.StatusCode -ne 200) { throw "Mini.MessageCenter /health failed: $($resp.StatusCode)" }
$resp = Call "https://localhost:5018/health"
if ($resp.StatusCode -ne 200) { throw "WebhookReceiverStub /health failed: $($resp.StatusCode)" }
Write-Host "  ✓ both healthy" -ForegroundColor Green

Write-Host ""
Write-Host "§2. Publish a PolicyChangedEvent for tenant 'acme' (via the Phase 20 diagnostic endpoint)" -ForegroundColor Yellow
$acmeMarker = "phase20-acme-" + [Guid]::NewGuid().ToString("N")
$acmeBody = @{ tenantKey = "acme"; resourceName = "sample-api"; newCondition = $acmeMarker } | ConvertTo-Json
$resp = Call "https://localhost:5017/api/v1/test/publish-policy-changed" "POST" $acmeBody
if ($resp.StatusCode -ne 202) { throw "Failed to publish acme event: $($resp.StatusCode) $($resp.Content)" }
Write-Host "  ✓ published (marker: $acmeMarker)" -ForegroundColor Green

Write-Host ""
Write-Host "§3. Publish a PolicyChangedEvent for tenant 'globex'" -ForegroundColor Yellow
$globexMarker = "phase20-globex-" + [Guid]::NewGuid().ToString("N")
$globexBody = @{ tenantKey = "globex"; resourceName = "sample-api"; newCondition = $globexMarker } | ConvertTo-Json
$resp = Call "https://localhost:5017/api/v1/test/publish-policy-changed" "POST" $globexBody
if ($resp.StatusCode -ne 202) { throw "Failed to publish globex event: $($resp.StatusCode) $($resp.Content)" }
Write-Host "  ✓ published (marker: $globexMarker)" -ForegroundColor Green

Write-Host ""
Write-Host "§4. WebhookReceiverStub (unscoped) receives BOTH events" -ForegroundColor Yellow
$deadline = (Get-Date).AddSeconds(20)
$sawAcme = $false
$sawGlobex = $false
$acmeEntry = $null
while ((Get-Date) -lt $deadline -and -not ($sawAcme -and $sawGlobex)) {
    Start-Sleep -Milliseconds 500
    $resp = Call "https://localhost:5018/webhook/received"
    if ($resp.StatusCode -ne 200) { continue }
    $entries = $resp.Content | ConvertFrom-Json
    foreach ($e in $entries) {
        if ($e.body -like "*$acmeMarker*") { $sawAcme = $true; $acmeEntry = $e }
        if ($e.body -like "*$globexMarker*") { $sawGlobex = $true }
    }
}
if (-not $sawAcme) { throw "WebhookReceiverStub never received the acme-tenant event." }
if (-not $sawGlobex) { throw "WebhookReceiverStub never received the globex-tenant event." }
Write-Host "  ✓ stub received both acme and globex events (unscoped subscription)" -ForegroundColor Green

Write-Host ""
Write-Host "§5. The stub's signature verification actually passed for the acme delivery" -ForegroundColor Yellow
if (-not $acmeEntry.signatureValid) { throw "WebhookReceiverStub logged the acme delivery but signatureValid was false." }
$expectedSignature = ComputeHmac $acmeEntry.body "stub-webhook-secret-do-not-use-in-prod"
if ($acmeEntry.signature -ne $expectedSignature) { throw "Recomputed HMAC signature does not match the one the stub verified." }
Write-Host "  ✓ HMAC-SHA256 signature verified independently by this script, not just trusted from the stub" -ForegroundColor Green

Write-Host ""
Write-Host "§6. Mini.MessageCenter's delivery history records both fan-outs" -ForegroundColor Yellow
$resp = Call "https://localhost:5017/api/v1/deliveries"
if ($resp.StatusCode -ne 200) { throw "Failed to read delivery history: $($resp.StatusCode)" }
$deliveries = $resp.Content | ConvertFrom-Json
$acmeDeliveries = $deliveries | Where-Object { $_.eventSummary -like "*$acmeMarker*" }
$globexDeliveries = $deliveries | Where-Object { $_.eventSummary -like "*$globexMarker*" }
# acme's event should have gone to exactly 2 subscriptions (acme-scoped + unscoped stub); globex's
# only to 1 (unscoped stub — acme's subscription must NOT appear, that's §7).
if ($acmeDeliveries.Count -ne 2) { throw "Expected 2 delivery attempts for the acme event (acme-scoped + unscoped), got $($acmeDeliveries.Count)." }
if ($globexDeliveries.Count -ne 1) { throw "Expected 1 delivery attempt for the globex event (unscoped only), got $($globexDeliveries.Count)." }
Write-Host "  ✓ acme event delivered to 2 subscriptions, globex event delivered to 1" -ForegroundColor Green

Write-Host ""
Write-Host "§7. Acme's scoped subscription did NOT receive the globex event (tenant scoping holds)" -ForegroundColor Yellow
$acmeSubscriptionId = "11111111-1111-1111-1111-111111111111"
$leaked = $globexDeliveries | Where-Object { $_.subscriptionId -eq $acmeSubscriptionId }
if ($leaked) { throw "Acme's scoped subscription received a globex-tenant event - tenant scoping is broken." }
Write-Host "  ✓ no cross-tenant leak: Acme's subscription never saw Globex's event" -ForegroundColor Green

Write-Host ""
Write-Host "Phase 20 verified." -ForegroundColor Green
Write-Host "  - PolicyChangedEvent consumed off the real RabbitMQ bus by Mini.MessageCenter" -ForegroundColor DarkGray
Write-Host "  - Unscoped subscription (WebhookReceiverStub) received every tenant's event" -ForegroundColor DarkGray
Write-Host "  - Scoped subscription (Mini.AcmeApi) would receive only acme's (delivery history confirms Acme's row was targeted only by the acme event)" -ForegroundColor DarkGray
Write-Host "  - HMAC-SHA256 signatures verified end to end" -ForegroundColor DarkGray
