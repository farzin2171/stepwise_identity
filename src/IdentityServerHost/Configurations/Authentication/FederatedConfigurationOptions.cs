namespace IdentityServerHost.Configurations.Authentication;

// IdG counterpart: Configurations/Authentication/FederatedConfigurationOptions.cs. Exists for the case a
// real deployment hits constantly and this sample's toy ExternalIdp never does: the provider you
// federate to is itself a BROKER (e.g. Azure AD B2C in front of a corporate Entra tenant), so the
// identifier you actually want isn't on the top-level token B2C hands you — it's nested inside an
// embedded token B2C passes through. Enabled + TokenName tell the callback which claim holds that nested
// token; ObjectIdClaimName says which claim inside THAT token is the real durable id.
//
// Not consumed anywhere yet in this sample — ExternalController.Callback() only ever sees a single-hop
// provider (ExternalIdp), so there's no nested token to unwrap. See
// docs/external-providers-configuration.md for what wiring this in for real would require.
public class FederatedConfigurationOptions
{
    public bool Enabled { get; set; }
    public string? TokenName { get; set; }
    public string? ObjectIdClaimName { get; set; }
}
