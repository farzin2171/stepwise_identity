namespace IdentityServerHost.Configurations.Authentication.OpenId;

// IdG counterpart: Configurations/Authentication/OpenId/IOpenIdConnectConfigurationOptions.cs.
//
// Introduced in Phase 9 for one specific reason. Before this phase there was exactly one source of OIDC
// provider settings (OpenIdConnectProviderOptions, bound from appsettings.json), so
// OpenIdConnectAuthenticationExtensions.AddOpenId could take that concrete type and shape the handler's
// options inline. Phase 9 adds a second source (OpenIdConnectProvider, read from the IdentityProviders
// table) carrying the same settings in a different container.
//
// Rather than duplicate ~40 lines of carefully-reasoned OpenIdConnectOptions setup — every line of which
// has a comment explaining a bug it prevents — both sources implement this interface and share one
// ConfigureOpenId method. Duplicating it would have meant the next SameSite or PAR fix landing in one
// copy and not the other, which is exactly the class of drift Phase 10 exists to clean up elsewhere.
public interface IOpenIdConnectConfigurationOptions
{
    string Authority { get; }

    string ClientId { get; }

    string ClientSecret { get; }

    string[] Scopes { get; }

    bool GetClaimsFromUserInfoEndpoint { get; }
}
