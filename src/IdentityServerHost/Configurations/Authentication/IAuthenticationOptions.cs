namespace IdentityServerHost.Configurations.Authentication;

// IdG counterpart: Configurations/Authentication/IAuthenticationOptions.cs. Every external provider type
// (AzureAd, AzureAdB2C, OpenId, Saml — only OpenId exists in this sample so far) implements this, so
// generic code (AuthenticationHelper, AccountController) can work with "some external provider" without
// knowing which concrete type it's looking at.
public interface IAuthenticationOptions
{
    // The scheme name this provider is registered under (what gets passed to Challenge(props, scheme)).
    string Name { get; }

    // What the login page shows on the button — "Sign in with {DisplayName}".
    string DisplayName { get; }

    // Which tenant this provider belongs to. This is the load-bearing difference from the mini-idg
    // Phase 4 sample's Tenants.AllowedExternalSchemes: there, "which schemes does tenant X get" was a
    // hardcoded dictionary keyed by tenant. Here, exactly like the real IdG, the provider declares its
    // OWN tenant, and anything that needs "all providers for tenant X" filters this list instead of
    // maintaining a second, parallel mapping that could drift out of sync with the provider list itself.
    string EcosystemTenant { get; }

    // Not yet wired into ExternalController — modeled now so the settings shape matches the real IdG and
    // the gap is visible, not because a caller uses it yet. See the "FederatedConfiguration" section of
    // docs/external-providers-configuration.md for what this is for and why it's still a placeholder here.
    FederatedConfigurationOptions? FederatedConfiguration { get; }

    // Same status as FederatedConfiguration above: modeled, not yet consumed. Real IdG counterpart:
    // ClaimsExtensions.ApplyClaimMappings, run before ExternalController does anything else with the
    // external principal's claims.
    IDictionary<string, string> ClaimMappings { get; }
}
