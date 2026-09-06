using Duende.IdentityServer.Stores;
using IdentityServerHost.IdentityServer.Models;
using Microsoft.Extensions.Options;

namespace IdentityServerHost.Configurations.Authentication.Helpers;

// IdG counterpart: Configurations/Authentication/Helpers/AuthenticationHelper.cs.
//
// Before Phase 9 this class had a comment saying "there, this also merges in dynamically-registered
// (database-backed) schemes via IIdentityProviderStore; this sample has no database yet (Phase 5)."
// That comment was stale the moment Phase 5 landed and the gap it described is what this phase closes.
//
// The merge order matters and matches the real IdG: file-based providers are collected first, then
// database-backed ones are appended. In the real IdG the ordering is load-bearing for a different reason
// (its GetAuthenticationOptionsAsync checks each config list before falling back to the store, so a
// file-based provider WINS a scheme-name collision). This sample keeps the same precedence for the same
// reason — a provider you can see in appsettings.json should never be silently overridden by a row.
public class AuthenticationHelper(
    IOptions<ExternalProvidersOptions> options,
    IIdentityProviderStore identityProviderStore,
    IConfiguration configuration,
    ILogger<AuthenticationHelper> logger) : IAuthenticationHelper
{
    public async Task<IEnumerable<IAuthenticationOptions>> GetAllAvailableIdentityProvidersAsync(string? tenantKey, CancellationToken ct = default)
    {
        if (tenantKey is null)
        {
            return [];
        }

        var providers = new List<IAuthenticationOptions>(
            options.Value.OpenId.Where(provider => provider.EcosystemTenant == tenantKey));

        // Same feature flag, same name, as the real IdG's Startup.cs. Off means the IdentityProviders
        // table is never read and this phase's behavior disappears entirely — which is what makes it
        // testable in both directions from one running app.
        if (!configuration.GetValue<bool>("DynamicIdentityProviderEnabled"))
        {
            return providers;
        }

        // GetAllSchemeNamesAsync returns names and enabled flags only — deliberately cheap, because it's
        // called on every login page render. Getting EcosystemTenant (which lives in the Properties bag)
        // means actually loading each provider, so filtering by tenant costs one read per enabled scheme.
        // The real IdG dodges this by filtering on the client's IdentityProviderRestrictions and a
        // per-tenant client property instead of on the provider's own tenant — see "Where this sample
        // simplifies" in the README for why that's a different design, not just a faster one.
        var schemes = await identityProviderStore.GetAllSchemeNamesAsync(ct);

        foreach (var scheme in schemes.Where(s => s.Enabled))
        {
            if (providers.Any(p => p.Name == scheme.Scheme))
            {
                logger.LogWarning(
                    "Identity provider scheme '{Scheme}' exists both in configuration and in the database. " +
                    "The configured one wins; the database row is ignored.", scheme.Scheme);
                continue;
            }

            if (await identityProviderStore.GetBySchemeAsync(scheme.Scheme, ct) is BaseIdentityProvider provider
                && provider.EcosystemTenant == tenantKey)
            {
                providers.Add(provider);
            }
        }

        return providers;
    }
}
