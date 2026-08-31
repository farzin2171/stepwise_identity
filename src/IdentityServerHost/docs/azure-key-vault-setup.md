# Setting up a real Azure Key Vault for signing-key management

This is the manual runbook for Phase 8 — everything the code in
[`../KeyManagement`](../KeyManagement) can't do for you, because it needs a real Azure
subscription. See [`../README.md`](../README.md)'s Phase 8 section for what the code
actually does and how it compares to the real IdG; this file is just the "how do I
actually get a vault" half.

Nothing in this repo provisions Azure resources for you — every command below is meant
to be run by hand, once, against your own subscription.

## Prerequisites

- An Azure subscription you can create resources in.
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) installed and
  logged in (`az login`).
- This sample's `KeyManagement:Provider` config (see `appsettings.json`) — you'll point
  it at whatever you create here.

## 1. Create a resource group and a Key Vault

```bash
az group create --name rg-stepwise-identity --location eastus

az keyvault create \
  --name kv-stepwise-identity \
  --resource-group rg-stepwise-identity \
  --location eastus \
  --enable-rbac-authorization true
```

`--enable-rbac-authorization true` opts into Azure RBAC for data-plane access (who can
read/write certificates and secrets) instead of the older vault "access policy" model.
RBAC is what this whole runbook assumes from here on — it's the model Microsoft
recommends for new vaults, and it's what actually let us verify the exact permission a
signing app needs (see step 4).

Key Vault names are globally unique across all of Azure — `kv-stepwise-identity` will
very likely already be taken. Pick your own.

## 2. Create the signing certificate

```bash
az keyvault certificate create \
  --vault-name kv-stepwise-identity \
  --name identityserver-signing \
  --policy "$(az keyvault certificate get-default-policy)"
```

`get-default-policy` gives you a self-signed, 1-year, RSA 2048 certificate — fine for
this course. A real production deployment would use a policy naming a real CA issuer
instead of `Self`, and would set up **auto-renewal** (`--policy` supports a
`lifetime_actions` block that re-issues automatically before expiry — not covered here,
since this course's `AzureKeyVaultKeyStore` only reacts to whatever versions already
exist in the vault, it doesn't request new ones).

Confirm it exists:

```bash
az keyvault certificate show --vault-name kv-stepwise-identity --name identityserver-signing \
  --query "{version:x509ThumbprintHex, notBefore:attributes.notBefore, expires:attributes.expires}"
```

## 3. Grant access — and the one gotcha that trips almost everyone up

Azure Key Vault has **two** separate object types for a certificate: the certificate
metadata itself, and — because a certificate includes a private key — a same-named
**secret** holding the full PFX. Reading a certificate's metadata and reading its
private key are two different permission checks, against two different resource types
(`certificates` vs. `secrets`).

This matters here because `AzureKeyVaultKeyStore.cs`'s
`CertificateClient.DownloadCertificateAsync(...)` — the call that gets a usable
`X509Certificate2` with its private key attached — reads from the **secrets** API
internally, not just the certificates API. Grant only certificate permissions and this
call fails with `Forbidden`, even though every certificate-only operation (listing
versions, reading metadata) worked fine moments before. This is a genuinely common,
confusing first encounter with Key Vault, not specific to this sample.

The good news, verified against this subscription's actual role definitions rather than
assumed: the built-in **`Key Vault Certificate User`** role already includes both.

```bash
az role definition list --name "Key Vault Certificate User" \
  --query "[0].permissions[0].dataActions" -o tsv
```

```
Microsoft.KeyVault/vaults/certificates/read
Microsoft.KeyVault/vaults/secrets/getSecret/action
Microsoft.KeyVault/vaults/secrets/readMetadata/action
Microsoft.KeyVault/vaults/keys/read
```

`secrets/getSecret/action` is right there — Microsoft designed this role to include it,
specifically because reading a certificate's private key needs it. One role assignment
is enough for the app; you don't need to separately grant a secrets role.

Grant it to whatever identity IdentityServerHost will run as (see step 4 for what that
identity actually is):

```bash
az role assignment create \
  --role "Key Vault Certificate User" \
  --assignee "<principal-id-or-client-id>" \
  --scope "$(az keyvault show --name kv-stepwise-identity --query id -o tsv)"
```

For **managing** certificates yourself (creating, rotating, deleting) — a different job
from what the app needs — use `Key Vault Certificates Officer` instead. That role does
**not** include secret access; it's scoped to certificate CRUD only. You (as the person
running the `az keyvault certificate create` command above) need this role on yourself,
separately from whatever role the app gets.

```bash
az role assignment create \
  --role "Key Vault Certificates Officer" \
  --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --scope "$(az keyvault show --name kv-stepwise-identity --query id -o tsv)"
```

## 4. Authenticate the app — two paths

`AzureKeyVaultKeyStore`'s constructor picks between these automatically, based on
whether `KeyManagement:AzureKeyVault:ClientSecret` is configured:

### Local dev: your own `az login` session (recommended, no secret to manage)

Leave `ClientId`/`ClientSecret`/`TenantId` blank. `DefaultAzureCredential` falls back
through several credential sources and finds your `az login` session automatically —
nothing to configure beyond the vault name. Grant the role in step 3 to **yourself**:

```bash
az role assignment create \
  --role "Key Vault Certificate User" \
  --assignee "$(az ad signed-in-user show --query id -o tsv)" \
  --scope "$(az keyvault show --name kv-stepwise-identity --query id -o tsv)"
```

This is exactly the same credential path `DefaultAzureCredential` uses for a **managed
identity** in a real Azure deployment (App Service, Container Apps, etc.) — locally it
falls back to your `az login` session instead, but the code path in
`AzureKeyVaultKeyStore` never changes between the two.

### A service principal (App Registration) — for anything non-interactive

```bash
az ad sp create-for-rbac --name sp-stepwise-identity-signing --skip-assignment
```

This prints `appId` (→ `ClientId`), `password` (→ `ClientSecret`), and `tenant` (→
`TenantId`). Grant the role to the new principal's **object id**, not its `appId`:

```bash
principalId=$(az ad sp show --id <appId-from-above> --query id -o tsv)
az role assignment create \
  --role "Key Vault Certificate User" \
  --assignee "$principalId" \
  --scope "$(az keyvault show --name kv-stepwise-identity --query id -o tsv)"
```

**Never commit the client secret.** Use `dotnet user-secrets` for local testing of this
path:

```bash
cd src/IdentityServerHost
dotnet user-secrets set "KeyManagement:AzureKeyVault:ClientSecret" "<password-from-above>"
```

## 5. Configure this sample to use it

`appsettings.Development.json` (or User Secrets, for the `ClientSecret`):

```json
"KeyManagement": {
  "Provider": "AzureKeyVault",
  "AzureKeyVault": {
    "VaultName": "kv-stepwise-identity",
    "CertificateName": "identityserver-signing",
    "TenantId": "",
    "ClientId": "",
    "ClientSecret": ""
  }
}
```

Leave `TenantId`/`ClientId`/`ClientSecret` blank for the `az login` path (step 4's first
option). `VaultName` is just the vault's name, not a full URL —
`AzureKeyVaultOptions`/`AzureKeyVaultKeyStore` build
`https://{VaultName}.vault.azure.net` from it.

## 6. Verify it actually worked

```bash
cd src/IdentityServerHost
dotnet run
```

```bash
curl -k https://localhost:5001/.well-known/openid-configuration/jwks
```

A `200` with a `keys` array means `AzureKeyVaultKeyStore` successfully resolved a
signing credential from the vault. Confirm it's genuinely *your* certificate, not the
developer key, by matching thumbprints:

```bash
az keyvault certificate show --vault-name kv-stepwise-identity --name identityserver-signing \
  --query x509ThumbprintHex -o tsv
```

The JWKS response's key doesn't expose the thumbprint directly, but its `x5t` field
(base64url-encoded SHA-1 thumbprint) should decode to the same bytes — or more simply,
run `pwsh ./test-phase2.ps1` from the repo root: if a login completes and the resulting
token verifies, IdentityServerHost is issuing real, Key Vault-signed tokens end to end.

## 7. Certificate rotation — the actual feature this course is teaching

Create a second version of the same certificate:

```bash
az keyvault certificate create \
  --vault-name kv-stepwise-identity \
  --name identityserver-signing \
  --policy "$(az keyvault certificate get-default-policy)"
```

This doesn't create a *new* certificate — it's a new **version** of the same one. Query
the versions:

```bash
az keyvault certificate list-versions --vault-name kv-stepwise-identity --name identityserver-signing \
  --query "[].{version:x509ThumbprintHex, created:attributes.created}"
```

Immediately after creating it, `jwks` still signs with the **old** version — the new
one hasn't cleared `RolloverDelayHours` (48 by default) yet, but it already shows up as
an additional **validation** key (this sample's cached response only refreshes every
`RefreshIntervalHours` — restart IdentityServerHost, or wait, to see it). Once the new
version is older than the rollover delay, it becomes the active signing key
automatically, with the old version still validating any tokens issued before the
switch. No code change, no restart-and-hope — the whole point of doing this over HTTP
against a real vault instead of a file on disk.

## 8. Managing and cleaning up

- **Soft delete is on by default** for new vaults — `az keyvault delete` doesn't
  immediately free the name; it enters a recoverable, soft-deleted state for a retention
  period (90 days by default). To actually reuse the name or stop any possibility of
  billing, purge it:

  ```bash
  az keyvault delete --name kv-stepwise-identity --resource-group rg-stepwise-identity
  az keyvault purge --name kv-stepwise-identity --location eastus
  ```

- **Cost**: Key Vault's certificate operations and secret/certificate storage are billed
  per-operation and are inexpensive for a course's worth of testing (a handful of
  dollars at most, typically pennies) — but not free, and it's a real resource in a real
  subscription. Delete/purge it when you're done unless you're deliberately keeping it
  around for further phases.
- **The App Registration** (if you created one in step 4) isn't deleted by
  `az keyvault delete` — clean it up separately:

  ```bash
  az ad sp delete --id <appId-from-step-4>
  ```

## Reference: config shape recap

| Key | Meaning | Blank means |
|---|---|---|
| `KeyManagement:Provider` | `"Developer"` or `"AzureKeyVault"` | `"Developer"` if unset |
| `KeyManagement:AzureKeyVault:VaultName` | Just the name, not a URL | — |
| `KeyManagement:AzureKeyVault:CertificateName` | The cert's name in the vault | — |
| `KeyManagement:AzureKeyVault:TenantId`/`ClientId`/`ClientSecret` | Service-principal auth | `DefaultAzureCredential` (your `az login`, or managed identity in Azure) |
| `KeyManagement:AzureKeyVault:RolloverDelayHours` | How long a new version waits before becoming the active signer | `48` |
| `KeyManagement:AzureKeyVault:RefreshIntervalHours` | How long a resolved key set is cached before asking the vault again | `24` |
