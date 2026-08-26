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
        new IdentityResources.Profile()
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
            UserClaims = { "name", "email" }
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

            RedirectUris = { "http://localhost:5002/signin-oidc" },
            PostLogoutRedirectUris = { "http://localhost:5002/signout-callback-oidc" },

            AllowedScopes =
            {
                IdentityServerConstants.StandardScopes.OpenId,
                IdentityServerConstants.StandardScopes.Profile,
                "api1"
            }
        }
    ];
}
