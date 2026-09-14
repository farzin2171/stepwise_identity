# Starts every process this sample needs, in the right order, and waits until each one actually answers
# on /health before moving on. Added in Phase 10, when the process count made "start five terminals by
# hand" the most error-prone step in the whole course.
#
# Why this exists rather than the Visual Studio launch profile (stepwise_identity.slnLaunch.user):
# that file is VS-only, and it can't express "run the migrations and the ingestion tool FIRST, then the
# hosts." That ordering used to live in prose spread across several READMEs.
#
#   .\run-all.ps1              start everything, wait for health, leave it running
#   .\run-all.ps1 -Stop        stop everything this script started
#   .\run-all.ps1 -SkipIngest  skip ConfigIngestionTool (faster if config hasn't changed)
#   .\run-all.ps1 -IncludeStub also start the superseded ExternalServicesStub on :5012
#
# Phase 11: ExternalServicesStub has LEFT the default set, as Phase 10 predicted it eventually would.
# Mini.UserService (:5013) took over the two calls IdentityServerHost makes during token issuance, and
# IdentityServerHost's ExternalServicesApi config now points there — so the stub is no longer on any
# path a login depends on. It is kept, not deleted (see src/ExternalServicesStub/README.md), and
# -IncludeStub starts it for a side-by-side comparison against the service that replaced it.
#
# Note what did NOT change: test-phase7.ps1 still passes, unmodified, and now exercises
# Mini.UserService instead of the stub. That is the actual proof the replacement is behaviour-
# preserving — same routes, same tenant GUIDs, same role table, different implementation behind them.
#
# ReactSpa is not started: it's `npm run dev`, not `dotnet run`, and no test-phase*.ps1 drives
# browser JavaScript. Start it by hand when you want to click through it.

param(
    [switch]$Stop,
    [switch]$SkipIngest,
    [switch]$IncludeStub
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$pidFile = Join-Path $root ".run-all.pids"

# Port numbers must match each project's launchSettings.json — see the phase conventions skill's
# "Known pre-existing gotcha" for why these drift and how to fix it when they do.
$services = @(
    @{ Name = "ExternalIdp";        Project = "src/ExternalIdp";        Url = "https://localhost:5011" }
    @{ Name = "IdentityServerHost"; Project = "src/IdentityServerHost"; Url = "https://localhost:5001" }
    @{ Name = "SampleApi";          Project = "src/SampleApi";          Url = "https://localhost:5007" }
    @{ Name = "Mini.UserService";   Project = "src/Mini.UserService";   Url = "https://localhost:5013" }
    # Phase 12. Acme's OWN user API - a WebApi connector target, and the first process in this repo
    # that stands in for a system a TENANT owns rather than one the platform owns. It is in the default
    # set because it is now on the login path for acme: Mini.UserService resolves acme's role claim
    # through a connector pointed here, so with this process down, alice's role silently falls back to
    # "Member" and test-phase7.ps1 fails. That is not fragility to work around - it is what taking a
    # dependency on a tenant's own system actually costs, and it is worth seeing.
    @{ Name = "Mini.AcmeApi";       Project = "src/Mini.AcmeApi";       Url = "https://localhost:5014" }
    # Phase 13. Mini.AuthorizationService (:5015) — the authorization decision service. Called by SampleApi
    # to evaluate policies. Not yet on the login path or a test dependency, but started by default so
    # test-phase13.ps1 can exercise it.
    @{ Name = "Mini.AuthorizationService"; Project = "src/Mini.AuthorizationService"; Url = "https://localhost:5015" }
    @{ Name = "MvcClient";          Project = "src/MvcClient";          Url = "https://localhost:5006" }
    # Phase 17. A SECOND MVC client, imitating Applications.Apply, with its own client registration
    # ("agentportal") on the same IdentityServerHost. Not yet on any other project's dependency path.
    @{ Name = "AgentPortal";        Project = "src/AgentPortal";        Url = "https://localhost:5016" }
)

if ($IncludeStub) {
    $services += @{ Name = "ExternalServicesStub"; Project = "src/ExternalServicesStub"; Url = "https://localhost:5012" }
}

function Stop-All {
    if (-not (Test-Path $pidFile)) {
        Write-Host "No .run-all.pids file - nothing this script started is being tracked." -ForegroundColor Yellow
        return
    }
    foreach ($line in Get-Content $pidFile) {
        $processId, $name = $line -split ",", 2
        try {
            Stop-Process -Id ([int]$processId) -Force -ErrorAction Stop
            Write-Host "  stopped $name (pid $processId)" -ForegroundColor Green
        } catch {
            Write-Host "  $name (pid $processId) was already gone" -ForegroundColor DarkGray
        }
    }
    Remove-Item $pidFile
}

if ($Stop) {
    Write-Host "Stopping everything run-all.ps1 started..."
    Stop-All
    return
}

if (Test-Path $pidFile) {
    throw "$pidFile already exists - something may already be running. Run '.\run-all.ps1 -Stop' first."
}

Write-Host "Building the solution..." -ForegroundColor Cyan
dotnet build (Join-Path $root "stepwise_identity.sln") -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed - fix that before starting anything." }

# Ordering that used to be tribal knowledge. IdentityServerHost migrates its own schema on startup, but
# it no longer seeds any rows (Phase 6), so the ingestion tool has to run before any login can succeed.
# It creates the database on first run too, via db.Database.MigrateAsync().
if (-not $SkipIngest) {
    Write-Host "Ingesting configuration (clients, resources, identity providers)..." -ForegroundColor Cyan
    Push-Location (Join-Path $root "src/Tools/ConfigIngestionTool")
    try {
        dotnet run --no-build
        if ($LASTEXITCODE -ne 0) { throw "ConfigIngestionTool failed." }
    } finally {
        Pop-Location
    }
}

$started = @()
foreach ($service in $services) {
    Write-Host "Starting $($service.Name) on $($service.Url)..." -ForegroundColor Cyan
    $process = Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--no-build", "--urls", $service.Url) `
        -WorkingDirectory (Join-Path $root $service.Project) `
        -PassThru -WindowStyle Hidden
    $started += "$($process.Id),$($service.Name)"
}

# Written before the health wait, not after: if a service never comes up, the pid file still exists so
# -Stop can clean up the ones that did.
Set-Content -Path $pidFile -Value $started

Write-Host ""
Write-Host "Waiting for /health on each service..." -ForegroundColor Cyan

# Trust every certificate for the duration of this script. Every URL here is localhost with a dev cert;
# a real client must not do this.
Add-Type -TypeDefinition @"
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class RunAllCertPolicy {
    public static void TrustAll() {
        ServicePointManager.ServerCertificateValidationCallback =
            delegate (object s, X509Certificate c, X509Chain ch, SslPolicyErrors e) { return true; };
    }
}
"@ -ErrorAction SilentlyContinue
[RunAllCertPolicy]::TrustAll()

$failed = @()
foreach ($service in $services) {
    $healthy = $false
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $response = Invoke-WebRequest -Uri "$($service.Url)/health" -TimeoutSec 2 -UseBasicParsing
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if ($healthy) {
        Write-Host "  $($service.Name) healthy" -ForegroundColor Green
    } else {
        Write-Host "  $($service.Name) NEVER became healthy at $($service.Url)/health" -ForegroundColor Red
        $failed += $service.Name
    }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "These never came up: $($failed -join ', ')" -ForegroundColor Red
    Write-Host "A port already in use is the usual cause - check for a stray dotnet process." -ForegroundColor Yellow
    Write-Host "Run '.\run-all.ps1 -Stop' to clean up." -ForegroundColor Yellow
    exit 1
}

Write-Host "Everything is up. Run any test-phase*.ps1 now." -ForegroundColor Green
Write-Host "Stop it all with: .\run-all.ps1 -Stop" -ForegroundColor DarkGray
