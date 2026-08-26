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

    // No clients yet — that's Phase 2.
    public static IEnumerable<Client> Clients => [];
}
