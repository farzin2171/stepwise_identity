namespace IdentityServerHost.Configurations.Authentication.OpenId;

// IdG counterpart: Configurations/Authentication/OpenId/OpenIdConnectConfigurationOptions.cs — the
// generic "any standard OIDC IdP" provider type (Okta, Auth0, Ping, or — in this sample — the toy
// ExternalIdp project). Both Microsoft Entra ID and Azure AD B2C could technically be configured through
// this same type by pointing Authority at the right URL directly; the real IdG instead gives each its own
// purpose-built type (AzureAdAuthenticationOptions, AzureAdB2CAuthenticationOptions) because they need
// extra protocol-specific behavior this plain type doesn't have — see
// docs/external-providers-configuration.md for what that extra behavior is and why it's out of scope for
// this sample's first step.
public class OpenIdConnectProviderOptions : BaseAuthenticationOptions
{
    // The OIDC issuer to trust — this app fetches {Authority}/.well-known/openid-configuration once and
    // caches it. For a real Entra ID tenant this looks like
    // https://login.microsoftonline.com/{tenantId}/v2.0 (see docs/azure-entra-b2c-setup.md).
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    // Belongs in appsettings.Development.json (or, for a real deployment, dotnet user-secrets / Key
    // Vault) — never appsettings.json, which is meant to be committed. See
    // docs/external-providers-configuration.md.
    public string ClientSecret { get; set; } = string.Empty;

    // Must exactly match a redirect URI registered with the provider — see
    // docs/external-providers-configuration.md's "CallbackPath" note for why this has to be an exact,
    // byte-for-byte match.
    public string CallbackPath { get; set; } = string.Empty;

    // Appended to the "openid" and "profile" scopes every registration gets by default — same behavior
    // as the real IdG's equivalent Scopes field (additive, so a client can never accidentally drop
    // "openid" by configuring this).
    public string[] Scopes { get; set; } = [];

    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;
}
