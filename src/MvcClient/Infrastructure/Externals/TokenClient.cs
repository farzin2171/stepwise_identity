using IdentityModel.Client;
using Microsoft.Extensions.Caching.Memory;
using MvcClient.Infrastructure.Configuration;

namespace MvcClient.Infrastructure.Externals;

// Apply counterpart: Equisoft.Apply.Data/Externals/Clients/TokenClient.cs — the "IdG as an OAuth2
// client-credentials provider" pattern, used everywhere Apply needs a SERVICE-ACCOUNT token rather than
// forwarding the signed-in user's own token (contrast with HomeController.CallApi(), which forwards the
// user's token — see docs/multitenancy-and-external-services.md for the two patterns side by side).
//
// The real client_id sent to /connect/token is "{serviceAccount.ClientId}.{tenantKey}" — e.g.
// "mvcclient-svc.acme" — with a PER-TENANT secret looked up from TenantSecrets. This is why
// IdentityServerHost/Configurations/IdentityServerConfig.json (Phase 6) registers two
// client-credentials clients instead of one: a real deployment gives every tenant its own
// client-credentials client and secret, so revoking or rotating one tenant's service-account access
// never touches another tenant's.
//
// Simplified from the real ServiceAccountTokenRepository: that one caches in IDistributedCache and
// re-validates freshness by decoding the cached JWT's own "exp" claim on every read. This uses IMemoryCache
// with an absolute expiration set from the token response's own expires_in instead — same effect (a
// request past expiry always fetches a fresh token), fewer moving parts, no JWT parsing needed to answer
// "is this still good."
public class TokenClient(IHttpClientFactory httpClientFactory, IMemoryCache cache) : ITokenClient
{
    public async Task<string> GetAccessTokenAsync(ServiceAccount serviceAccount, string tenantKey, CancellationToken ct = default)
    {
        var clientId = $"{serviceAccount.ClientId}.{tenantKey}";
        var cacheKey = $"serviceAccountToken:{clientId}";

        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        if (!serviceAccount.TenantSecrets.TryGetValue(tenantKey, out var clientSecret))
        {
            throw new InvalidOperationException(
                $"No service-account secret configured for tenant '{tenantKey}' (ServiceAccount:TenantSecrets).");
        }

        var httpClient = httpClientFactory.CreateClient("token");
        var response = await httpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
        {
            Address = serviceAccount.TokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret
        }, ct);

        if (response.IsError || response.AccessToken is null)
        {
            throw new InvalidOperationException(
                $"Failed to get a service-account token for client '{clientId}': {response.Error}");
        }

        // 30-second safety margin so a token doesn't expire mid-flight between the cache read and the
        // actual downstream call it's about to authorize.
        var ttl = TimeSpan.FromSeconds(Math.Max(response.ExpiresIn - 30, 30));
        cache.Set(cacheKey, response.AccessToken, ttl);

        return response.AccessToken;
    }
}
