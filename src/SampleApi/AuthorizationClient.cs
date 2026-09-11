using System.Text.Json;
using Mini.Infrastructure.Identity;

public interface IAuthorizationClient
{
    Task<AuthorizationResult> EvaluateAsync(string resourceName, IIdentityContext identity, Dictionary<string, string>? context = null);
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

    public async Task<AuthorizationResult> EvaluateAsync(string resourceName, IIdentityContext identity, Dictionary<string, string>? context = null)
    {
        try
        {
            var request = new
            {
                resourceName,
                subject = identity.Subject,
                context = context ?? new Dictionary<string, string>()
            };

            var content = new StringContent(
                JsonSerializer.Serialize(request),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync("/api/v1/authorization/evaluate", content);

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
}
