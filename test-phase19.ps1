# Proves Phase 19's message-bus port actually works against a REAL RabbitMQ instance, not just
# MassTransit's in-memory test harness (that proof already lives in
# tests/StepwiseIdentity.Tests/MessageBusTests.cs and runs on every `dotnet test`).
#
# Unlike every other test-phaseN.ps1, this phase adds no HTTP surface of its own (no new service,
# no new controller) - so there's nothing to drive with raw HTTP here. What there IS to prove is
# that MessageBusExtensions.AddMessageBus's RabbitMQ branch really connects, publishes, and a
# consumer really receives, over the wire. The cleanest way to do that without over-building is to
# reuse the xunit project: RabbitMqMessageBusTests is tagged [Trait("Category", "RequiresRabbitMQ")]
# and excluded from the default `dotnet test` run precisely so it can be invoked separately, here,
# against a real broker.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

Write-Host "Starting RabbitMQ (docker compose up -d)..." -ForegroundColor Cyan
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Docker doesn't appear to be running (docker info failed). Start Docker Desktop / your " +
        "Docker engine and try again."
}

docker compose -f (Join-Path $root "docker-compose.yml") up -d
if ($LASTEXITCODE -ne 0) { throw "docker compose up failed - see output above." }

Write-Host "Waiting for RabbitMQ's management API on :15672..." -ForegroundColor Cyan
$healthy = $false
for ($i = 0; $i -lt 60; $i++) {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:15672/api/health/checks/alarms" `
            -Headers @{ Authorization = "Basic " + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("guest:guest")) } `
            -TimeoutSec 2 -UseBasicParsing
        if ($response.StatusCode -eq 200) { $healthy = $true; break }
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
if (-not $healthy) {
    throw "RabbitMQ never became healthy on :15672 - check 'docker compose logs rabbitmq'."
}
Write-Host "  RabbitMQ healthy" -ForegroundColor Green

Write-Host ""
Write-Host "Running RabbitMqMessageBusTests against the real broker..." -ForegroundColor Cyan
Push-Location (Join-Path $root "tests/StepwiseIdentity.Tests")
try {
    dotnet test --filter "Category=RequiresRabbitMQ" --nologo
    if ($LASTEXITCODE -ne 0) { throw "The real-RabbitMQ round-trip test failed - see output above." }
} finally {
    Pop-Location
}

Write-Host ""
Write-Host "Phase 19 verified: PolicyChangedEvent published and consumed over a real RabbitMQ instance." -ForegroundColor Green
Write-Host "Stop RabbitMQ with: docker compose down (or leave it running - run-all.ps1 -Stop also stops it)" -ForegroundColor DarkGray
