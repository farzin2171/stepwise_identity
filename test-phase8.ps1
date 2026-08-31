# Verifies Phase 8: the default "KeyManagement:Provider=Developer" still signs tokens normally after
# adding the Azure Key Vault code path - the one thing this script can safely automate without
# restarting a service mid-script (every other test-phaseN.ps1 only ever talks to already-running
# services; this is the first phase where "switch providers" inherently means stopping and restarting
# IdentityServerHost with different config, which doesn't fit that pattern).
#
# Run IdentityServerHost (and its other dependencies - see the root README) first.

$ErrorActionPreference = "Stop"

Write-Host "1. Confirming the default (KeyManagement:Provider=Developer) still signs tokens normally..." -ForegroundColor Cyan
$jwks = Invoke-WebRequest -Uri "https://localhost:5001/.well-known/openid-configuration/jwks" -SkipCertificateCheck -UseBasicParsing
if ($jwks.StatusCode -ne 200) { throw "Expected 200 from jwks with the developer key, got $($jwks.StatusCode)" }
$keys = ($jwks.Content | ConvertFrom-Json).keys
if (-not $keys -or $keys.Count -eq 0) { throw "Expected at least one signing key in the jwks response" }
Write-Host "   PASS - jwks endpoint answers normally, $($keys.Count) key(s) published" -ForegroundColor Green

Write-Host ""
Write-Host "PHASE 8 SIGNING-KEY MANAGEMENT (DEVELOPER PATH): PASS" -ForegroundColor Green
Write-Host ""
Write-Host "Not scripted (needs stopping/restarting IdentityServerHost with different config, which no" -ForegroundColor Yellow
Write-Host "other test-phaseN.ps1 does either): proving the AzureKeyVault path is really wired up, not a" -ForegroundColor Yellow
Write-Host "silent fallback. Try it yourself:" -ForegroundColor Yellow
Write-Host "" -ForegroundColor Yellow
Write-Host "  1. Stop IdentityServerHost." -ForegroundColor Yellow
Write-Host "  2. cd src/IdentityServerHost" -ForegroundColor Yellow
Write-Host "     `$env:KeyManagement__Provider = `"AzureKeyVault`"" -ForegroundColor Yellow
Write-Host "     `$env:KeyManagement__AzureKeyVault__VaultName = `"nonexistent-vault-xyz123`"" -ForegroundColor Yellow
Write-Host "     `$env:KeyManagement__AzureKeyVault__CertificateName = `"test-cert`"" -ForegroundColor Yellow
Write-Host "     dotnet run" -ForegroundColor Yellow
Write-Host "  3. curl -k https://localhost:5001/.well-known/openid-configuration/jwks" -ForegroundColor Yellow
Write-Host "     Expect a 500 whose stack trace shows AzureKeyVaultKeyStore genuinely trying to reach" -ForegroundColor Yellow
Write-Host "     nonexistent-vault-xyz123.vault.azure.net (DNS resolution failure) - proof it tried" -ForegroundColor Yellow
Write-Host "     Key Vault for real, not a silent fallback to the developer key." -ForegroundColor Yellow
Write-Host "  4. Ctrl+C, unset those three env vars, dotnet run again to restore the developer key." -ForegroundColor Yellow
Write-Host "" -ForegroundColor Yellow
Write-Host "For an actual successful Key Vault round trip (real vault, real certificate, real rotation)," -ForegroundColor Yellow
Write-Host "see src/IdentityServerHost/docs/azure-key-vault-setup.md." -ForegroundColor Yellow
