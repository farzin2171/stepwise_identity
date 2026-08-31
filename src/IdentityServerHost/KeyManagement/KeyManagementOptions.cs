namespace IdentityServerHost.KeyManagement;

// Bind target for the "KeyManagement" config section. IdG counterpart: "KeyManagementProvider"
// (a bare string, not nested under a parent section) plus "AzureKeyManagementProvider" as its own
// top-level section — combined here into one section for a smaller config surface. The real system
// also has a third provider, "Local" (a cert-file path, for on-premise deployments) — not ported here;
// this course only needed the two ends of the real spectrum (throwaway dev key, real Key Vault).
public class KeyManagementOptions
{
    public string Provider { get; set; } = "Developer";
    public AzureKeyVaultOptions AzureKeyVault { get; set; } = new();
}

// IdG counterpart: AzureProvisioningOptions (Configurations/Certificates/AzureProvisioningOptions.cs).
// VaultName is stored, not a full URL — same as the real system's AzureKeyVaultOptions.KeyVaultUrl,
// computed as "https://{Name}.vault.azure.net" rather than typed in full each time.
public class AzureKeyVaultOptions
{
    public string VaultName { get; set; } = "";
    public string CertificateName { get; set; } = "";

    // Blank ClientId/ClientSecret => DefaultAzureCredential (managed identity in Azure, `az login`
    // context for local dev). Filled in => ClientSecretCredential. Same branch the real
    // AzureKeyVaultClientFactory makes — see docs/azure-key-vault-setup.md for how to set either up.
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    // A cert version becomes the ACTIVE signing key only once it's been enabled for at least this
    // long — gives every relying party's cached JWKS response time to pick up the new key as a
    // VALIDATION key before it's ever used to SIGN anything, so no token is ever signed with a key
    // some caller hasn't seen yet.
    public int RolloverDelayHours { get; set; } = 48;

    // How long a resolved (signing credential, validation keys) pair is cached before this store
    // asks Key Vault again — same real, deliberate consequence the never-expiring TenantClient cache
    // (Phase 7) already puts on display: a permission or certificate change in Key Vault isn't picked
    // up faster than this, on purpose, matching the real system's own trade-off.
    public int RefreshIntervalHours { get; set; } = 24;
}
