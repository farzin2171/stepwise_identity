using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mini.Infrastructure.Identity;
using Microsoft.Extensions.Logging;

namespace Mini.Infrastructure.ExternalServices;

// Extracted from SampleApi in Phase 16 — the first consumer to reuse this was Mini.AuthorizationService's
// second caller (Phase 17's Agent Portal), which made a copy-paste-or-extract decision arrive right on
// schedule with Mini.Infrastructure's own founding rule (see this project's README).
//
// Loosely modeled on Libraries.Infrastructure/DIT.Authorization.Client, but that library is a Refit
// interface (IAuthorizationServiceClientV1.AuthorizeAsync/EvaluateAsync) wired into ASP.NET Core's own
// IAuthorizationPolicyProvider, so a real DIT service expresses "is this caller allowed" as an ordinary
// [Authorize(Policy = "...")] attribute — the HTTP call to the authorization service happens inside a
// policy handler the framework invokes, not as an explicit client call in application code. This sample
// keeps the shape SampleApi already had instead: an explicit EvaluateAsync call the endpoint makes itself,
// against Mini.AuthorizationService's own resource/context evaluation shape, which has no Refit/policy-name
// counterpart to port from. Porting the policy-provider integration itself is future work, not this phase's.
public interface IAuthorizationClient
{
    Task<AuthorizationResult> EvaluateAsync(string resourceName, IIdentityContext identity, string bearerToken, Dictionary<string, string>? context = null);
    Task<string> ClearCacheAsync(string tenantKey, string serviceAccountToken);
}

public record AuthorizationResult(bool Authorized, string Reason);

public class AuthorizationClient(HttpClient httpClient, ILogger<AuthorizationClient> logger) : IAuthorizationClient
{
    public async Task<AuthorizationResult> EvaluateAsync(string resourceName, IIdentityContext identity, string bearerToken, Dictionary<string, string>? context = null)
    {
        try
        {
            var request = new
            {
                resourceName,
                subject = identity.Subject,
                context = context ?? new Dictionary<string, string>()
            };

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/authorization/evaluate")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var response = await httpClient.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var authorized = root.GetProperty("authorized").GetBoolean();
                var reason = root.GetProperty("reason").GetString() ?? "Unknown";

                logger.LogInformation("Authorization evaluation for {ResourceName} tenant {TenantKey}: {Authorized}",
                    resourceName, identity.TenantKey, authorized);

                return new AuthorizationResult(authorized, reason);
            }
            else
            {
                logger.LogError("Authorization service returned {StatusCode} for {ResourceName}",
                    response.StatusCode, resourceName);
                return new AuthorizationResult(false, "Authorization service error");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling authorization service for {ResourceName}", resourceName);
            return new AuthorizationResult(false, "Authorization service unavailable");
        }
    }

    public async Task<string> ClearCacheAsync(string tenantKey, string serviceAccountToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/authorization/cache/{tenantKey}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceAccountToken);

            var response = await httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Authorization service cache-clear returned {StatusCode} for tenant {TenantKey}",
                    response.StatusCode, tenantKey);
                return $"Authorization service returned {(int)response.StatusCode} clearing cache for '{tenantKey}'.";
            }

            var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.GetProperty("message").GetString() ?? "Cache cleared.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling authorization service to clear cache for tenant {TenantKey}", tenantKey);
            return "Authorization service unavailable — cache not cleared.";
        }
    }
}
