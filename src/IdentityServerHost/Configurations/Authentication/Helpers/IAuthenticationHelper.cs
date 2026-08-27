namespace IdentityServerHost.Configurations.Authentication.Helpers;

public interface IAuthenticationHelper
{
    // IdG counterpart: AuthenticationHelper.GetAllAvailableIdentityProviders. Returns every registered
    // external provider whose EcosystemTenant matches tenantKey — an empty tenantKey (no tenant resolved
    // at all) always returns none, the same fail-closed default the real IdG's IsTenantParameterRequired
    // path uses when a client needs a tenant and none was supplied.
    IEnumerable<IAuthenticationOptions> GetAllAvailableIdentityProviders(string? tenantKey);
}
