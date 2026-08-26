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

    // No APIs to protect yet.
    public static IEnumerable<ApiScope> ApiScopes => [];

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

            AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile }
        }
    ];
}
