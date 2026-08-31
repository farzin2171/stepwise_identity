using Duende.IdentityServer.Models;

namespace ExternalIdp;

// This whole project stands in for a real external identity provider (a partner's Entra tenant, an SSO
// broker, whatever the real IdG federates with). It's a second, independent Duende IdentityServer —
// nothing here is aware that "mini-idg" or "tenants" exist. From this project's point of view,
// IdentityServerHost is just another OIDC client.
public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile()
    ];

    public static IEnumerable<ApiScope> ApiScopes => [];

    public static IEnumerable<Client> Clients =>
    [
        new Client
        {
            ClientId = "mini-idg-host",
            ClientName = "Mini IdG — acting as a client of this external IdP",
            ClientSecrets = { new Secret("external-secret".Sha256()) },

            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,
            RequireConsent = false,

            RedirectUris = { "https://localhost:5001/signin-external-idp" },

            AllowedScopes =
            {
                Duende.IdentityServer.IdentityServerConstants.StandardScopes.OpenId,
                Duende.IdentityServer.IdentityServerConstants.StandardScopes.Profile
            }
        },

        // Phase 9. A second registration, for the same external IdP, used by the DATABASE-backed provider
        // that Initech gets. Two things make it a separate client rather than another redirect URI on the
        // one above.
        //
        // First, the redirect URI shape is different and not negotiable: a dynamic provider's callback is
        // /federation/{scheme}/signin, computed by Duende from the scheme name, where a file-based one
        // uses whatever CallbackPath you configured. Change the scheme name in the database and this URI
        // changes with it — which is a real operational trap, because the external IdP's registration
        // then has to be updated to match.
        //
        // Second, it's what a real deployment looks like: two tenants federating to the same partner IdP
        // get their own client registrations and their own secrets, not a shared one.
        new Client
        {
            ClientId = "mini-idg-host-initech",
            ClientName = "Mini IdG (Initech, database-backed provider) — acting as a client of this external IdP",
            ClientSecrets = { new Secret("external-secret-initech".Sha256()) },

            AllowedGrantTypes = GrantTypes.Code,
            RequirePkce = true,
            RequireConsent = false,

            RedirectUris = { "https://localhost:5001/federation/initech-external-idp/signin" },

            AllowedScopes =
            {
                Duende.IdentityServer.IdentityServerConstants.StandardScopes.OpenId,
                Duende.IdentityServer.IdentityServerConstants.StandardScopes.Profile
            }
        }
    ];
}
