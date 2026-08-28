namespace MvcClient.Infrastructure.Configuration;

// Apply counterpart: Equisoft.Apply.Domain/Configuration/ServiceAccount.cs, verbatim shape. Notice this
// lives nested inside ExternalServicesConfiguration (below), not IdentityGatewayConfiguration — even
// though the token endpoint IS the IdG's — matching the real appsettings.json layout exactly, confusing
// as that placement is on first read.
public class ServiceAccount
{
    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;

    // Real IdG counterpart: TokenClient builds the actual client_id sent to /connect/token as
    // "{ClientId}.{tenantKey}" and looks up the matching secret here — see
    // IdentityServerHost/Config.cs for the two client registrations this requires
    // ("mvcclient-svc.acme", "mvcclient-svc.globex") and Externals/TokenClient.cs for where this gets used.
    public Dictionary<string, string> TenantSecrets { get; set; } = new();
}
