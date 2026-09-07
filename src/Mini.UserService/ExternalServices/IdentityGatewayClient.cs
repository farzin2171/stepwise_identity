using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Mini.Infrastructure.ExternalServices;

namespace Mini.UserService.ExternalServices;

// Real counterpart: Services.User's IIdentityClientV1 — GET /user/convert/{userId}?convertTo=
// {conversionType} against the Identity Gateway, authenticated with a SERVICE-ACCOUNT token
// ("ServiceAccounts:*" config, acquired and cached).
//
// This is the first thing in this repo to call the IdG as a plain OAuth client-credentials client
// rather than as a login-flow participant, and it is what Phase 10 extracted
// Mini.Infrastructure/ExternalServices/TokenClient.cs for. Phase 10's own table predicted the consumer
// would be IdentityServerHost. It's this service instead — the direction of the call was wrong in the
// prediction, the need for the client wasn't.
public class IdentityGatewayClient(
    IHttpClientFactory httpClientFactory,
    ITokenClient tokenClient,
    IOptions<ExternalServicesConfiguration> options)
{
    public async Task<string?> ConvertUserIdAsync(
        string userId,
        ConversionType conversionType,
        string tenantKey,
        CancellationToken ct = default)
    {
        var serviceAccount = options.Value.GetServiceDefinition("IdentityGateway").ServiceAccount
            ?? throw new InvalidOperationException(
                "ExternalServicesApi:ServiceAccount is not configured — see appsettings.Development.json.");

        // Per-tenant client id and secret: TokenClient sends "{ClientId}.{tenantKey}", so this resolves
        // to "userservice-svc.acme" / "userservice-svc.globex" / "userservice-svc.initech". Three
        // registered clients with three secrets instead of one shared credential, so revoking one
        // tenant's service-account access never touches another's — the same reasoning behind
        // MvcClient's own "mvcclient-svc.{tenant}" pair since Phase 7.
        var accessToken = await tokenClient.GetAccessTokenAsync(serviceAccount, tenantKey, ct);

        var client = httpClientFactory.CreateClient("IdentityGateway");
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/user/convert/{Uri.EscapeDataString(userId)}?convertTo={conversionType}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request, ct);

        // A 404 here means "the IdG has no such mapping," which is an answer, not a failure — it
        // becomes this service's own 404. Anything else is a genuine fault and is allowed to throw, so
        // a misconfigured service account surfaces as a 500 with a real exception in the log instead of
        // an indistinguishable empty 404.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ConvertResponse>(ct);
        return body?.ConvertedUserId;
    }

    private record ConvertResponse(string ConvertedUserId);
}

// Mirrors the real API's conversionType parameter. "External" means "give me the provider's own
// subject id for this local user"; "Local" means the reverse.
public enum ConversionType
{
    Local,
    External
}
