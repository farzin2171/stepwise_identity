using System.Net.Http.Headers;
using Duende.IdentityServer;
using Microsoft.Extensions.Options;

namespace IdentityServerHost.ExternalServices;

// Real IdG counterpart: Data/Externals/Clients/TenantClient.cs — calls the DIT Tenant Management
// service to resolve a tenant key ("acme") to its real database GUID. Caching (and the never-expiring
// bug this course reproduces on purpose) lives one level up, in SampleProfileService — same split as
// the real system, where TenantClient itself is a plain HTTP client and EquisoftTokenResponseGenerator
// owns the cache.
public class TenantClient(HttpClient httpClient, IIdentityServerTools tools, IOptions<ExternalServicesOptions> options)
{
    public async Task<string> GetTenantAsync(string tenantKey, CancellationToken ct = default)
    {
        var tenantOptions = options.Value.Tenant;

        // IdentityServerHost acting as its OWN OAuth client — no /connect/token round trip, just a
        // JWT signed with the same key every other token in this sample is signed with. The target
        // service (ExternalServicesStub) trusts that key because it's the same one it already
        // validates every other token against.
        var jwt = await tools.IssueClientJwtAsync(
            tenantOptions.JwtAuthentication.ClientId,
            lifetime: 300,
            ct,
            audiences: [tenantOptions.JwtAuthentication.Audience]);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{tenantOptions.Address}/v1/tenants/GetByKey/{tenantKey}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<TenantResponse>(ct)
            ?? throw new InvalidOperationException($"Tenant service returned an empty body for key '{tenantKey}'.");
        return body.TenantId;
    }

    private record TenantResponse(string TenantId);
}
