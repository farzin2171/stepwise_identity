using Microsoft.Extensions.Options;

namespace IdentityServerHost.Configurations.Authentication.Helpers;

// IdG counterpart: Configurations/Authentication/Helpers/AuthenticationHelper.cs. There, this also
// merges in dynamically-registered (database-backed) schemes via IIdentityProviderStore; this sample has
// no database yet (Phase 5), so it only ever has the file-based list bound at startup.
public class AuthenticationHelper(IOptions<ExternalProvidersOptions> options) : IAuthenticationHelper
{
    public IEnumerable<IAuthenticationOptions> GetAllAvailableIdentityProviders(string? tenantKey)
    {
        if (tenantKey is null)
        {
            return [];
        }

        return options.Value.OpenId.Where(provider => provider.EcosystemTenant == tenantKey);
    }
}
