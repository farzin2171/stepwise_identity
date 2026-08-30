# Verifies Phase 5: IdentityServerHost's clients/resources and grants now live in SQL Server (LocalDB)
# instead of in-memory, and ExternalUserStore now persists federated identities the same way.
# This script re-runs the Phase 2-4 flows against the DB-backed server (proving nothing regressed),
# then queries the database directly to prove the seed/provisioning actually landed in SQL Server.
#
# Run IdentityServerHost, ExternalIdp, MvcClient, SampleApi and ReactSpa first (see the root README's
# "Running it" section), then run this script.
#
# What this script CANNOT prove by itself: that state survives a process restart - that's the actual
# point of Phase 5, and needs a real restart, not a scripted one (starting a new dotnet process mid-script
# wouldn't demonstrate anything a fresh `dotnet run` doesn't already do). See "Manual verification" below.

$ErrorActionPreference = "Stop"

Write-Host "1. Re-running Phase 2-4 against the DB-backed server..." -ForegroundColor Cyan
& "$PSScriptRoot/test-phase2.ps1" | Out-Null
Write-Host "   test-phase2.ps1 PASS" -ForegroundColor Green
& "$PSScriptRoot/test-phase3.ps1" | Out-Null
Write-Host "   test-phase3.ps1 PASS" -ForegroundColor Green
& "$PSScriptRoot/test-phase4.ps1" | Out-Null
Write-Host "   test-phase4.ps1 PASS (also provisions Carol into ExternalUserStore, checked below)" -ForegroundColor Green

Write-Host ""
Write-Host "2. Querying SQL Server (LocalDB) directly to confirm the seed/provisioning actually landed..." -ForegroundColor Cyan

$clientCount = (sqlcmd -S "(localdb)\mssqllocaldb" -d MiniIdG -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Clients" -C).Trim()
if ([int]$clientCount -lt 4) { throw "Expected at least 4 seeded clients in ConfigurationDbContext, found $clientCount" }
Write-Host "   PASS - $clientCount clients found in the Clients table (seeded from Config.cs, not in-memory)" -ForegroundColor Green

$carolCount = (sqlcmd -S "(localdb)\mssqllocaldb" -d MiniIdG -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM Users WHERE SubjectId = 'external:external-idp:ext-1'" -C).Trim()
if ([int]$carolCount -ne 1) { throw "Expected Carol's provisioned row in Users, found $carolCount" }
Write-Host "   PASS - Carol's federated identity (external:external-idp:ext-1) is a real row in the Users table" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 5 PERSISTENCE: PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Manual verification (the part a script can't prove): stop IdentityServerHost, start it again," -ForegroundColor Yellow
Write-Host "then log in as alice/bob and re-run the ExternalIdp federation for carol WITHOUT re-running any" -ForegroundColor Yellow
Write-Host "seed step yourself - if it all still works with no extra setup, state survived the restart." -ForegroundColor Yellow
