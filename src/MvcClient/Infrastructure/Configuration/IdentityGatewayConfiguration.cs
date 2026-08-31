namespace MvcClient.Infrastructure.Configuration;

// Apply counterpart: Equisoft.Apply.Domain/Configuration/IdentityGatewayConfiguration.cs — same field
// names, same GetRequestUri fallback logic, bound from the same "IdentityGatewayApi" config section name.
// In this sample TenantUrls is empty for both tenants (there's only one IdentityServerHost), so
// GetRequestUri always falls back to Url — but the mechanism is real and testable: add a fake entry
// pointing "acme" somewhere unreachable and watch the login redirect actually go there. See
// docs/multitenancy-and-external-services.md.
public class IdentityGatewayConfiguration
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> TenantUrls { get; set; } = new();
    public string ClientId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;

    public string GetRequestUri(string tenantKey) =>
        TenantUrls.TryGetValue(tenantKey, out var tenantUrl) && !string.IsNullOrWhiteSpace(tenantUrl)
            ? tenantUrl
            : Url;
}
