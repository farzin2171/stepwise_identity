using System.Net.Http.Headers;
using Duende.IdentityServer;
using Microsoft.Extensions.Options;

namespace IdentityServerHost.ExternalServices;

// Real IdG counterpart: Data/Externals/Clients/UserClient.cs — calls the DIT User service to resolve a
// caller's role. Deliberately NOT cached, in either system — the direct contrast to TenantClient's
// cached (and, on purpose, never-expiring) lookup. Same self-issued-JWT auth pattern as TenantClient;
// see its comments for why no client secret is involved.
//
// Phase 12 added the tenantKey argument, and it is the only change this host needed for the whole
// connectors phase — which is the point. Mini.UserService now decides where a role comes from by
// looking up that tenant's connector rows, and this host neither knows nor cares whether the answer
// came from Acme's own web API, a claim on a token, or the UserIdentityRoles table it used to be the
// only source of. All it has to do is say who is asking on whose behalf.
//
// Passing it is necessary because the self-issued JWT carries no tenant claim (CONTEXT.md, "Self-issued
// JWT"), so the callee cannot work it out. Nullable, and omitted from the query string when null: the
// callee then takes its pre-Phase-12 path, which is what keeps a login with no tenant hint at all
// (test-phase3.ps1 §4) working exactly as before.
public class UserClient(HttpClient httpClient, IIdentityServerTools tools, IOptions<ExternalServicesOptions> options)
{
    public async Task<string> GetRoleAsync(string subjectId, string? tenantKey, CancellationToken ct = default)
    {
        var userOptions = options.Value.User;

        var jwt = await tools.IssueClientJwtAsync(
            userOptions.JwtAuthentication.ClientId,
            lifetime: 300,
            ct,
            audiences: [userOptions.JwtAuthentication.Audience]);

        var tenantQuery = tenantKey is null ? "" : $"?tenant={Uri.EscapeDataString(tenantKey)}";
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{userOptions.Address}/v2/User/identities/role/{Uri.EscapeDataString(subjectId)}{tenantQuery}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);

        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(ct);
    }
}
