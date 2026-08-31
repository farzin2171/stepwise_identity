using IdentityServerHost.Configurations.Authentication.OpenId;

namespace IdentityServerHost.Configurations.Authentication;

// Bind target for the whole "ExternalProviders" config section. IdG counterpart:
// Configurations/Authentication/ExternalProvidersOptions.cs — there it also has AzureAdB2C, AzureAd, and
// Saml2 lists. This sample only implements OpenId so far (that's what ExternalIdp actually is); adding a
// real Entra ID or Azure AD B2C tenant means adding the matching list here plus a provider-options class
// and an Add*() extension under Configurations/Authentication/{AzureAd,AzureAdB2C}/ — same shape as
// OpenId's, not a different mechanism. See docs/external-providers-configuration.md.
public class ExternalProvidersOptions
{
    public List<OpenIdConnectProviderOptions> OpenId { get; set; } = [];
}
