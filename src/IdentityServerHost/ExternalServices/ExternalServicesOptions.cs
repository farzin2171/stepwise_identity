namespace IdentityServerHost.ExternalServices;

// Bind target for the "ExternalServicesApi" config section — real-IdG counterpart: the same section
// name, in that app's own appsettings.json, driving TenantClient/UserClient the same way.
public class ExternalServicesOptions
{
    public ExternalServiceOptions Tenant { get; set; } = new();
    public ExternalServiceOptions User { get; set; } = new();
}

public class ExternalServiceOptions
{
    public string Address { get; set; } = "";
    public JwtAuthenticationOptions JwtAuthentication { get; set; } = new();
}

// Not real OAuth client credentials — ClientId here only becomes the "client_id" claim on a
// self-issued JWT (IIdentityServerTools.IssueClientJwtAsync), never sent through /connect/token. No
// secret exists because none is needed: the token is signed by IdentityServerHost's own key, and the
// target service already trusts that key implicitly (it's the same key every other token in this
// sample is signed with).
public class JwtAuthenticationOptions
{
    public string ClientId { get; set; } = "";
    public string Audience { get; set; } = "";
}
