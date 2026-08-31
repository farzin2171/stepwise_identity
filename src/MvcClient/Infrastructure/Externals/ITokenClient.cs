using MvcClient.Infrastructure.Configuration;

namespace MvcClient.Infrastructure.Externals;

// Apply counterpart: Equisoft.Apply.Data/Externals/Clients/ITokenClient.cs.
public interface ITokenClient
{
    // tenantKey is explicit here (not read from ITenantContext internally) so this client stays testable
    // without a whole HTTP request/DI scope around it — same reasoning TokenClient.GetAccessTokenAsync
    // takes a ServiceAccount parameter instead of resolving IOptions<T> itself.
    Task<string> GetAccessTokenAsync(ServiceAccount serviceAccount, string tenantKey, CancellationToken ct = default);
}
