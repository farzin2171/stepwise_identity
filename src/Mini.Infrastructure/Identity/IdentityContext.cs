using System.Security.Claims;

namespace Mini.Infrastructure.Identity;

public class IdentityContext : IIdentityContext
{
    public bool IsAuthenticated { get; private set; }
    public IdentityType IdentityType { get; private set; }
    public string? Subject { get; private set; }
    public string? ClientId { get; private set; }
    public string? TenantKey { get; private set; }

    public void Populate(ClaimsPrincipal principal)
    {
        IsAuthenticated = principal.Identity?.IsAuthenticated == true;
        if (!IsAuthenticated)
        {
            return;
        }

        // The real service tells User and Service callers apart with an explicit
        // "service_isService" claim (Libraries.Infrastructure/DIT.Identity). IdentityServerHost never
        // stamps an equivalent claim here — but a client-credentials grant has no user behind it at
        // all, so it never gets a "sub" claim either. Absence of "sub" is this sample's (accurate,
        // if implicit) stand-in for that explicit flag.
        Subject = principal.FindFirst("sub")?.Value;
        ClientId = principal.FindFirst("client_id")?.Value;
        IdentityType = Subject is null ? IdentityType.Service : IdentityType.User;

        TenantKey = IdentityType switch
        {
            // Requires the client to have requested BOTH "api1" and "tenant" — see
            // IdentityServerHost/Configurations/IdentityServerConfig.json's ApiResource("api1").UserClaims for why "tenant_id" reaches
            // the access token at all, not just the ID token.
            IdentityType.User => principal.FindFirst("tenant_id")?.Value,

            // The real service reads a "service_tenant" claim stamped onto service-account tokens
            // (Libraries.Infrastructure/DIT.Identity/IdentityPrincipalClaimDefaults). IdentityServerHost's
            // client-credentials clients don't carry one (see Config.cs) — tenant is baked into the
            // client_id's suffix instead ("mvcclient-svc.acme"), so it's parsed from there instead.
            IdentityType.Service => ClientId?.Split('.', 2) switch
            {
                [_, var tenant] => tenant,
                _ => null
            },

            _ => null
        };
    }
}
