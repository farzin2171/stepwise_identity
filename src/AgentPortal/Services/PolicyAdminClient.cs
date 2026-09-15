using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentPortal.Services;

// Phase 22. A second, small typed client alongside Mini.Infrastructure's shared IAuthorizationClient —
// deliberately NOT added to that shared project. IAuthorizationClient's one method (EvaluateAsync) is a
// genuinely shared concern (SampleApi and AgentPortal both ask "am I authorized," Phase 16's whole
// reason to extract it). Reading/writing a Policy row's Condition is not shared with any other
// consumer in this repo yet — Mini.AuthorizationService's admin endpoint (Phase 21) has exactly one
// caller so far, this controller — so this stays local per "Shared concerns go in Mini.Infrastructure"
// (CONTEXT.md / the phase skill): port need-driven, not ahead of a second real consumer.
public interface IPolicyAdminClient
{
    Task<PolicyDto?> GetPolicyAsync(string resourceName, string bearerToken);
    Task<PolicyUpdateOutcome> UpdatePolicyAsync(string tenantKey, string resourceName, string condition, string bearerToken);
}

// Note what's missing from GET /api/v1/authorization/policies before this phase: Condition itself.
// Mini.AuthorizationService's Phase 13 projection only ever returned Id/Name/ResourceName/
// Description/IsEnabled/Order — nothing needed it until now. Phase 22 added Condition to that same
// endpoint's projection (Program.cs) rather than adding a second, near-duplicate GET, per the prompt's
// own "prefer reusing what's there" guidance.
public record PolicyDto(Guid Id, string Name, string ResourceName, string? Description, bool IsEnabled, int Order, string? Condition);

public record PolicyUpdateOutcome(bool Success, string? FailureDetail);

public class PolicyAdminClient(HttpClient httpClient, ILogger<PolicyAdminClient> logger) : IPolicyAdminClient
{
    public async Task<PolicyDto?> GetPolicyAsync(string resourceName, string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/authorization/policies");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Mini.AuthorizationService returned {StatusCode} listing policies for {ResourceName}",
                response.StatusCode, resourceName);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        var policies = JsonSerializer.Deserialize<List<PolicyDto>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return policies?.FirstOrDefault(p => p.ResourceName == resourceName);
    }

    public async Task<PolicyUpdateOutcome> UpdatePolicyAsync(string tenantKey, string resourceName, string condition, string bearerToken)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { condition });
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/authorization/policies/{tenantKey}/{resourceName}")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                return new PolicyUpdateOutcome(true, null);
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            logger.LogError("Mini.AuthorizationService returned {StatusCode} updating {TenantKey}/{ResourceName}: {Body}",
                response.StatusCode, tenantKey, resourceName, responseBody);
            return new PolicyUpdateOutcome(false, $"{(int)response.StatusCode} {response.ReasonPhrase}: {responseBody}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling Mini.AuthorizationService to update {TenantKey}/{ResourceName}", tenantKey, resourceName);
            return new PolicyUpdateOutcome(false, $"Authorization service unavailable: {ex.Message}");
        }
    }
}
