namespace IdentityServerHost.Configurations.Authentication.Helpers;

public interface IAuthenticationHelper
{
    // IdG counterpart: AuthenticationHelper.GetAllAvailableIdentityProviders. Returns every registered
    // external provider whose EcosystemTenant matches tenantKey — an empty tenantKey (no tenant resolved
    // at all) always returns none, the same fail-closed default the real IdG's IsTenantParameterRequired
    // path uses when a client needs a tenant and none was supplied.
    //
    // Became async in Phase 9: file-based providers are already in memory, but database-backed ones have
    // to be read from the IdentityProviders table. The signature change rippled out to AccountController,
    // which is the honest cost of the feature and worth noticing — a synchronous interface is a bet that
    // no future implementation will ever need I/O.
    Task<IEnumerable<IAuthenticationOptions>> GetAllAvailableIdentityProvidersAsync(string? tenantKey, CancellationToken ct = default);
}
