using Duende.IdentityServer.Hosting.DynamicProviders;
using IdentityServerHost.IdentityServer.Models;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace IdentityServerHost.Configurations.Authentication.OpenId;

// IdG counterpart: Configurations/Authentication/OpenId/OpenIdConnectConfigureOptions.cs.
//
// This is the database-backed path's answer to AddOpenId. The file-based path registers a scheme once at
// startup, when the settings are already in memory; a dynamic provider can't do that, because the row
// might be inserted, edited, or disabled while the app is running. So Duende inverts it: when a request
// arrives for /federation/{scheme}/..., it loads the row through IdentityProviderStore, hands the mapped
// provider to this class, and asks it to build the OpenIdConnectOptions on the spot.
//
// Duende's ConfigureAuthenticationOptions<TOptions, TIdentityProvider> base class does all the plumbing;
// the only thing left to write is the body below. And that body is three lines, because Phase 9 extracted
// ConfigureOpenId — every SameSite, PKCE, and PAR decision made in Phases 2 and 4 applies here without
// being restated.
public class OpenIdConnectConfigureOptions(
    IHttpContextAccessor httpContextAccessor,
    ILogger<ConfigureAuthenticationOptions<OpenIdConnectOptions, OpenIdConnectProvider>> logger)
    : ConfigureAuthenticationOptions<OpenIdConnectOptions, OpenIdConnectProvider>(httpContextAccessor, logger)
{
    protected override void Configure(ConfigureAuthenticationContext<OpenIdConnectOptions, OpenIdConnectProvider> context)
    {
        // context.PathPrefix is /federation/{scheme}, computed by Duende from the scheme name. This is the
        // callback path the external IdP must have registered — see OpenIdConnectProvider's comment on why
        // a dynamic provider can't just pick its own like the file-based one does.
        var callbackUrl = context.PathPrefix + "/signin";

        context.AuthenticationOptions.ConfigureOpenId(context.IdentityProvider, callbackUrl);
    }
}
