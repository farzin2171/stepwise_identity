using System.Text.Json;
using Mini.Infrastructure.Identity;

public interface IAuthorizationClient
{
    Task<AuthorizationResult> EvaluateAsync(string resourceName, IIdentityContext identity, string bearerToken, Dictionary<string, string>? context = null);
    Task<string> ClearCacheAsync(string tenantKey, string serviceAccountToken);
}

public record AuthorizationResult(bool Authorized, string Reason);

public class AuthorizationClient : IAuthorizationClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AuthorizationClient> _logger;

    public AuthorizationClient(HttpClient httpClient, ILogger<AuthorizationClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

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
                Content = new StringContent(JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

            var response = await _httpClient.SendAsync(httpRequest);

            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var authorized = root.GetProperty("authorized").GetBoolean();
                var reason = root.GetProperty("reason").GetString() ?? "Unknown";

                _logger.LogInformation("Authorization evaluation for {ResourceName} tenant {TenantKey}: {Authorized}",
                    resourceName, identity.TenantKey, authorized);

                return new AuthorizationResult(authorized, reason);
            }
            else
            {
                _logger.LogError("Authorization service returned {StatusCode} for {ResourceName}",
                    response.StatusCode, resourceName);
                return new AuthorizationResult(false, "Authorization service error");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling authorization service for {ResourceName}", resourceName);
            return new AuthorizationResult(false, "Authorization service unavailable");
        }
    }

    public async Task<string> ClearCacheAsync(string tenantKey, string serviceAccountToken)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/authorization/cache/{tenantKey}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", serviceAccountToken);

            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Authorization service cache-clear returned {StatusCode} for tenant {TenantKey}",
                    response.StatusCode, tenantKey);
                return $"Authorization service returned {(int)response.StatusCode} clearing cache for '{tenantKey}'.";
            }

            var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.GetProperty("message").GetString() ?? "Cache cleared.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling authorization service to clear cache for tenant {TenantKey}", tenantKey);
            return "Authorization service unavailable — cache not cleared.";
        }
    }
}
