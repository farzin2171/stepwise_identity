using Duende.IdentityServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace IdentityServerHost.Configurations.Authentication.OpenId;

// IdG counterpart: Configurations/Authentication/OpenId/OpenIdConnectAuthenticationExtensions.cs. The
// actual ASP.NET Core registration behind one "OpenId" entry in the ExternalProviders config section —
// called once per entry by ExternalProviderAuthenticationExtensions.AddExternalProvidersFromFile.
public static class OpenIdConnectAuthenticationExtensions
{
    public static AuthenticationBuilder AddOpenId(this AuthenticationBuilder builder, OpenIdConnectProviderOptions provider) =>
        builder.AddOpenIdConnect(provider.Name, provider.DisplayName, options =>
        {
            // Makes this a FEDERATED login rather than a replacement for local login: the result lands
            // on the external cookie, which ExternalController reads from once and then discards — see
            // its own comment for why this one line is "the whole trick."
            options.SignInScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;

            options.Authority = provider.Authority;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.CallbackPath = provider.CallbackPath;

            options.ResponseType = "code";
            // Explicit, not just the spec default for "code" alone: without this, a real IdP is free to
            // reply via form_post instead of a query-string redirect. form_post is a cross-site POST back
            // to this app, and SameSite=Lax (below) only rides along on cross-site *GET* navigation — a
            // POST would silently drop the correlation/nonce cookies and surface as "Correlation failed."
            // Forcing query keeps every hop a GET, matching PushedAuthorizationBehavior.Disable's same
            // "everything visible, nothing implicit" reasoning just below.
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.RequireHttpsMetadata = false; // local teaching sample only — see docs/azure-entra-b2c-setup.md
            options.SaveTokens = true;

            // Every external provider is itself a Duende IdentityServer, so it advertises a
            // pushed_authorization_request_endpoint too — the handler's default (UseIfAvailable) would
            // switch this hop to PAR automatically, exactly the same MvcClient-to-IdentityServerHost
            // gotcha documented in MvcClient's README ("The PAR gotcha this surfaced"), just one layer
            // further out. Disabled here for the same reason: this sample deliberately keeps every OIDC
            // hop's parameters visible in the URL rather than pushed server-side behind an opaque
            // request_uri, and consistently so — not just on the one hop that happened to break first.
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

            options.Scope.Clear();
            options.Scope.Add("openid");
            options.Scope.Add("profile");
            foreach (var scope in provider.Scopes)
            {
                options.Scope.Add(scope);
            }

            options.GetClaimsFromUserInfoEndpoint = provider.GetClaimsFromUserInfoEndpoint;
            options.MapInboundClaims = false;

            // Same relaxation IdentityServerHost's own cookies and MvcClient's OIDC handler already
            // need — see IdentityServerHost/README.md's Phase 2 gotcha #1.
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
}
