# Verifies Phase 6: Configurations/IdentityServerConfig.json (not the database) is now the source of
# truth for Clients/Resources - re-ingesting it with ConfigIngestionTool overwrites whatever drifted in
# the database, rather than leaving it alone.
#
# Run IdentityServerHost, ExternalIdp, MvcClient and SampleApi first (see the root README's
# "Running it" section) - this script needs a real login to prove the restored config actually works,
# not just that the row looks right.

$ErrorActionPreference = "Stop"
$sqlServer = "(localdb)\mssqllocaldb"
$database = "MiniIdG"

function Get-RequireConsent {
    (sqlcmd -S $sqlServer -d $database -h -1 -Q "SET NOCOUNT ON; SELECT RequireConsent FROM Clients WHERE ClientId = 'mvcclient'" -C).Trim()
}

Write-Host "1. Confirming mvcclient's RequireConsent starts out correct (0, per the JSON)..." -ForegroundColor Cyan
$before = Get-RequireConsent
if ($before -ne "0") { throw "Expected RequireConsent=0 before corrupting it, found '$before' - run ConfigIngestionTool once first." }
Write-Host "   PASS - RequireConsent=0" -ForegroundColor Green

Write-Host ""
Write-Host "2. Simulating drift: flipping RequireConsent to 1 directly in the database..." -ForegroundColor Cyan
sqlcmd -S $sqlServer -d $database -Q "UPDATE Clients SET RequireConsent = 1 WHERE ClientId = 'mvcclient'" -C | Out-Null
$corrupted = Get-RequireConsent
if ($corrupted -ne "1") { throw "Expected the direct UPDATE to stick (RequireConsent=1), found '$corrupted'" }
Write-Host "   Confirmed corrupted - RequireConsent=1 now, disagreeing with the JSON file" -ForegroundColor Yellow

Write-Host ""
Write-Host "3. Re-running ConfigIngestionTool - the JSON file is authoritative, so this should overwrite the drift..." -ForegroundColor Cyan
Push-Location "$PSScriptRoot/src/Tools/ConfigIngestionTool"
try {
    $output = dotnet run --no-build 2>&1
    Write-Host ($output | ForEach-Object { "   $_" })
}
finally {
    Pop-Location
}

$restored = Get-RequireConsent
if ($restored -ne "0") { throw "Expected re-ingestion to restore RequireConsent=0, found '$restored'" }
Write-Host "   PASS - RequireConsent restored to 0" -ForegroundColor Green

Write-Host ""
Write-Host "4. Proving the restored client isn't just a correct-looking row - it actually logs in..." -ForegroundColor Cyan
& "$PSScriptRoot/test-phase2.ps1" | Out-Null
Write-Host "   PASS - test-phase2.ps1's full login flow still succeeds against the re-ingested client" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 6 DATA INGESTION: PASS" -ForegroundColor Green
