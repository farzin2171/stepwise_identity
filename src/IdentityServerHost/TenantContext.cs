namespace IdentityServerHost;

// Request-scoped holder for whatever TenantResolutionMiddleware resolved. The real IdG has no single
// object like this — it re-derives tenant per call, in at least three different places (ITenantAccessor
// from a scheme name at login-page time, AuthenticationHelper from acr_values, and
// EquisoftTokenResponseGenerator from claims at token-issuance time). This sample centralizes resolution
// into one middleware for teaching clarity — a simplification, not a literal mirror.
public class TenantContext
{
    public string? TenantKey { get; set; }
    public string? DisplayName { get; set; }
    public bool HasTenant => TenantKey is not null;
}
