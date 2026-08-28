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
        }
    ];
}
