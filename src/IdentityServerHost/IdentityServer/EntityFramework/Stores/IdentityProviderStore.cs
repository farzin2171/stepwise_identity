using Duende.IdentityServer.EntityFramework.Interfaces;
using Duende.IdentityServer.EntityFramework.Mappers;
using Duende.IdentityServer.Models;
using IdentityServerHost.IdentityServer.Models;
using IdentityServerHost.Models.Constants;

namespace IdentityServerHost.IdentityServer.EntityFramework.Stores;

// IdG counterpart: IdentityServer/EntityFramework/Stores/IdentityProviderStore.cs — ported very nearly
// verbatim, which is itself the lesson. Note what this class does NOT do: it doesn't query the database,
// doesn't cache, doesn't handle enabled/disabled. All of that is inherited from Duende's own EF store.
//
// The single override is MapIdp: Duende's base implementation returns a generic OidcProvider for
// everything, which would throw away every custom property (EcosystemTenant, ClaimMappings, ...) the row
// carries. Overriding the mapper — rather than reimplementing the store — is the smallest possible hook
// that turns "a row" into "a strongly-typed provider this application understands."
//
// Program.cs registers this with .AddIdentityProviderStore<IdentityProviderStore>(). Until Phase 9 that
// line didn't exist, and Program.cs said so out loud: "no custom IClientStore/IResourceStore (it doesn't
// have those either; only a custom IdentityProviderStore, not yet ported here)."
public class IdentityProviderStore(
    IConfigurationDbContext context,
    ILogger<Duende.IdentityServer.EntityFramework.Stores.IdentityProviderStore> logger,
    IIdentityProviderFactory identityProviderFactory)
    : Duende.IdentityServer.EntityFramework.Stores.IdentityProviderStore(context, logger, identityProviderFactory)
{
    protected override IdentityProvider MapIdp(Duende.IdentityServer.EntityFramework.Entities.IdentityProvider idp)
    {
        return idp.Type switch
        {
            IdentityProviderTypes.OpenIdConnect => new OpenIdConnectProvider(idp.ToModel()),

            // Deliberately a throw, not a null or a silent skip — copied from the real IdG, and worth
            // keeping. A row whose Type nobody handles is a configuration error someone made on purpose;
            // failing loudly at the moment the scheme is resolved beats a login button that mysteriously
            // isn't there. The real IdG's message is reproduced almost word for word.
            _ => throw new Exception(
                $"Error while trying to map identity provider {idp.Scheme}. The type '{idp.Type}' is not supported")
        };
    }
}
