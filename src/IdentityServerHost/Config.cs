using Duende.IdentityServer;
using Duende.IdentityServer.Models;

namespace IdentityServerHost;

// This is the "shape without the data": the same Duende model types
// (IdentityResource, ApiScope, Client) that the real IdG loads from SQL
// Server, just declared in code instead. Duende's in-memory stores below
// don't care where these objects come from — that's the point of the
// store abstraction.
public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile(),
        // Opt-in, unlike the real IdG: EquisoftTokenResponseGenerator stamps tenantId into every token
        // unconditionally there. Here, a client has to ask for "tenant" like any other scope — see
        // Tenants.cs for why this sample simplifies three real-system components into one.
        new IdentityResource { Name = "tenant", DisplayName = "Tenant", UserClaims = { "tenant_id" } }
    ];

    // The one API this IdentityServer protects: SampleApi. "api1" is both the scope a client asks for
    // and (via the ApiResource below) the "aud" claim SampleApi checks on every incoming token.
    public static IEnumerable<ApiScope> ApiScopes =>
    [
        new ApiScope("api1", "Sample API access")
    ];

    // Without an ApiResource, Duende issues access tokens with no "aud" claim at all — SampleApi would
    // have nothing to check. This is what makes "api1" the audience, not just a scope name.
    // UserClaims here is what puts "name"/"email" INTO THE ACCESS TOKEN itself — by default an access
    // token carries only protocol claims (sub, scope, client_id, ...), not the identity-resource claims
    // that ended up in the ID token via "profile". An API and an ID token don't automatically see the
    // same claims; each has to ask for what it needs.
    public static IEnumerable<ApiResource> ApiResources =>
    [
        new ApiResource("api1", "Mini IdG Sample API")
        {
            Scopes = { "api1" },
            // "tenant_id" added for SampleApi's IIdentityContext port (see its
            // Infrastructure/Identity/IdentityContext.cs) — without it here, the "tenant" IdentityResource's
            // claim reaches the ID token (MvcClient's own login) but never the access token SampleApi
            // actually validates, same reasoning as "name"/"email" below.
            UserClaims = { "name", "email", "tenant_id" }
        }
    ];

    public static IEnumerable<Client> Clients =>
    [
        new Client
        {
            ClientId = "mvcclient",
            ClientSecrets = { new Secret("secret".Sha256()) },

            AllowedGrantTypes = GrantTypes.Code,
            // Required unconditionally, even for a confidential client with a secret — PKCE also closes
            // authorization-code interception on the redirect back to the client, not just the "public
            // client with no secret" hole it was originally designed for.
            RequirePkce = true,
            RequireConsent = false,

            RedirectUris = { "https://localhost:5006/signin-oidc" },
            PostLogoutRedirectUris = { "https://localhost:5006/signout-callback-oidc" },

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "api1",
                "tenant"
            }
        },
        new Client
        {
            ClientId = "reactspa",
            // The whole reason this client looks different from mvcclient: this app is static files a
            // browser downloads and runs. There is no server to hold a secret on, so there's no secret —
            // PKCE alone (not secret + PKCE) is what protects its authorization code exchange.
            RequireClientSecret = false,

            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,

            RedirectUris = { "http://localhost:5173/callback" },
            PostLogoutRedirectUris = { "http://localhost:5173" },
            // The one field mvcclient never needed. Duende reads this and wires up CORS for every one of
            // its endpoints automatically — without it, the browser's preflight OPTIONS request to
            // /connect/token gets no Access-Control-Allow-Origin header back, and the real POST never
            // leaves the browser at all.
            AllowedCorsOrigins = { "http://localhost:5173" },

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "api1",
                "tenant"
            }
        },
        // Apply counterpart: this is what its ServiceAccount/TokenClient pattern registers against — a
        // client-credentials client PER TENANT, not one shared client. "mvcclient-svc" (MvcClient's
        // ServiceAccount:ClientId in appsettings) plus the tenant key becomes the actual client_id sent to
        // /connect/token, so revoking or rotating one tenant's service-account access never touches
        // another's. No user is involved in this grant at all — see MvcClient's
        // docs/multitenancy-and-external-services.md for what calls this.
        new Client
        {
            ClientId = "mvcclient-svc.acme",
            ClientSecrets = { new Secret("acme-svc-secret".Sha256()) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "api1" }
        },
        new Client
        {
            ClientId = "mvcclient-svc.globex",
            ClientSecrets = { new Secret("globex-svc-secret".Sha256()) },
            AllowedGrantTypes = GrantTypes.ClientCredentials,
            AllowedScopes = { "api1" }
        }
    ];
}
