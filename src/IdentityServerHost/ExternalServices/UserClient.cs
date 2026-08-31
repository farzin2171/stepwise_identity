using System.Net.Http.Headers;
using Duende.IdentityServer;
using Microsoft.Extensions.Options;

namespace IdentityServerHost.ExternalServices;

// Real IdG counterpart: Data/Externals/Clients/UserClient.cs — calls the DIT User service to resolve a
// caller's role. Deliberately NOT cached, in either system — the direct contrast to TenantClient's
// cached (and, on purpose, never-expiring) lookup. Same self-issued-JWT auth pattern as TenantClient;
// see its comments for why no client secret is involved.
public class UserClient(HttpClient httpClient, IIdentityServerTools tools, IOptions<ExternalServicesOptions> options)
{
    public async Task<string> GetRoleAsync(string subjectId, CancellationToken ct = default)
    {
        var userOptions = options.Value.User;

        var jwt = await tools.IssueClientJwtAsync(
            userOptions.JwtAuthentication.ClientId,
            lifetime: 300,
            ct,
            audiences: [userOptions.JwtAuthentication.Audience]);

        var request = new HttpRequestMessage(HttpMethod.Get, $"{userOptions.Address}/v2/User/identities/role/{subjectId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }
}
