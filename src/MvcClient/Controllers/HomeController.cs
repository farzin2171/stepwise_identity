using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MvcClient.Infrastructure.Configuration;
using MvcClient.Infrastructure.Externals;
using MvcClient.Infrastructure.MultiTenant;

namespace MvcClient.Controllers;

public class HomeController(
    IHttpClientFactory httpClientFactory,
    ITenantContext tenantContext,
    ITokenClient tokenClient,
    IOptions<ExternalServicesConfiguration> externalServicesOptions) : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    [RequireTenant]
    public IActionResult Secure() => View((User.Claims, tenantContext.Tenant));

    // The only place in this app that says "this login is for a specific tenant." [Authorize] alone
    // (on Secure(), above) triggers a challenge with NO acr_values at all — IdentityServerHost has no
    // tenant hint, so its login page shows local-login-only, same as before Phase 3 existed. This action
    // is what a real tenant-aware client actually does: set acr_values BEFORE redirecting, so
    // IdentityServerHost's TenantResolutionMiddleware has something to resolve from the very first
    // request, not just on retry.
    [HttpGet]
    public IActionResult LoginAsTenant(string tenant)
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Secure)),
            // Read back by the OnRedirectToIdentityProvider hook in Program.cs, which builds BOTH the
            // tenant-correct Authority URL and the "acr_values=tenant:acme" hint from this one raw key.
            Items = { ["tenant"] = tenant }
        };

        return Challenge(props, "oidc");
    }

    // Demonstrates a client calling a protected API on the signed-in user's behalf: the access token
    // IdentityServerHost issued to THIS app (during login, because "api1" was in the requested scopes)
    // gets forwarded as a Bearer token. SampleApi never talks to IdentityServerHost or this app directly
    // to check it — it validates the token's signature, issuer, audience, and scope entirely on its own.
    // Apply counterpart: AuthorizationServiceClientV1 / AssistantManagementServiceClient — both forward
    // the current user's own JWT rather than fetching a service-account token.
    [Authorize]
    [RequireTenant]
    public async Task<IActionResult> CallApi()
    {
        // SaveTokens = true (Program.cs) is what makes this token available here — it's stored inside
        // this app's own auth cookie alongside the claims, not fetched fresh on every request.
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (accessToken is null)
        {
            return View("ApiResult", "No access token found on the current session — sign out and back in.");
        }

        return await CallSampleApiAsync(accessToken, "the signed-in user's own token");
    }

    // The other half of the pattern CallApi() demonstrates: a call made with NO user present at all,
    // authenticated as this tenant's SERVICE ACCOUNT instead. Apply counterpart:
    // ConfigurationServiceClientV1 / UserServiceClient — both fetch a service-account token via
    // ServiceAccountTokenRepository rather than forwarding a user's own. Compare this response to
    // CallApi()'s: SampleApi's claims table looks noticeably different because a client-credentials
    // token has no "sub", no "name", no "email" — there's no user behind it to have any.
    [Authorize]
    [RequireTenant]
    public async Task<IActionResult> CallApiAsServiceAccount()
    {
        var tenant = tenantContext.Tenant!; // RequireTenant already guaranteed this is non-null
        var serviceAccount = externalServicesOptions.Value.ServiceAccount
            ?? throw new InvalidOperationException("ExternalServicesApi:ServiceAccount is not configured.");

        var accessToken = await tokenClient.GetAccessTokenAsync(serviceAccount, tenant.Key);
        return await CallSampleApiAsync(accessToken, $"a service-account token for tenant '{tenant.Key}'");
    }

    private async Task<IActionResult> CallSampleApiAsync(string accessToken, string tokenDescription)
    {
        var client = httpClientFactory.CreateClient("SampleApi");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode && body.Length > 0)
        {
            using var parsed = JsonDocument.Parse(body);
            body = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
        }

        return View("ApiResult", $"HTTP {(int)response.StatusCode} {response.StatusCode}\nCalled with {tokenDescription}.\n\n{body}");
    }
}
