using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors;

// Real counterpart: DIT.Connectors' IClaimConnector — "read it off the JWT instead of calling out."
// The library map lists it as one of the three connector types; the dit-architecture reference for
// Services.User is candid that its handler usage isn't fully documented ("IClaimConnector
// (interface/repository present; handler usage not fully detailed in the repo)"). So: the schema and
// the idea are ported, the wiring below is this sample's own reading of it, and that distinction is
// stated rather than blurred.
//
// Why a connector type at all, when it makes no call? Because it belongs in the same decision table.
// "Where does this tenant's user data come from" and "this tenant's IdP already put it on the token"
// are answers to the SAME question, and modelling the second one as a connector means a tenant can
// move between them with a SQL update instead of a code change. That is the whole argument for the
// catalog/choice/settings design, made by its least impressive member.
public interface IClaimConnector
{
    Task<ServiceExtensibilityResult<string>> GetClaimValueAsync(
        ConnectorTenant tenant,
        string handlerName,
        CancellationToken ct = default);
}

public class ClaimConnector(
    IClaimConnectorConfigurationsRepository configurations,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ClaimConnector> logger) : IClaimConnector
{
    public async Task<ServiceExtensibilityResult<string>> GetClaimValueAsync(
        ConnectorTenant tenant,
        string handlerName,
        CancellationToken ct = default)
    {
        var claimName = await configurations.GetClaimNameAsync(tenant.TenantId, handlerName, ct);
        if (claimName is null)
        {
            // Same reasoning as WebApiConnector's missing route: chose Claim, never said which claim.
            return ServiceExtensibilityResult<string>.Failed(
                $"Tenant '{tenant.Key}' selected a Claim connector for '{handlerName}' but has no claim name configured.");
        }

        // IHttpContextAccessor rather than IIdentityContext, and that is not laziness: IIdentityContext
        // (Mini.Infrastructure/Identity) exposes four resolved properties — IsAuthenticated,
        // IdentityType, Subject, ClientId, TenantKey — and deliberately not the raw claims, because its
        // whole point is that callers stop re-reading ClaimsPrincipal by hand. A connector whose
        // configuration names an arbitrary claim at runtime cannot be served by a fixed set of
        // properties. So this is the one place in the repo that legitimately needs the principal itself.
        var principal = httpContextAccessor.HttpContext?.User;
        var value = principal?.FindFirst(claimName)?.Value;

        if (string.IsNullOrEmpty(value))
        {
            // Succeeded(null), not Failed. "The configured claim is not on this token" is an answer —
            // and in this sample it is the USUAL answer, for a reason worth understanding:
            //
            // IdentityServerHost calls this service during token issuance with a SELF-ISSUED JWT
            // (IIdentityServerTools.IssueClientJwtAsync), which carries iss, nbf, iat, exp, client_id
            // and aud and nothing else — see CONTEXT.md's "Self-issued JWT". There is no "role" claim
            // on it and there never will be, because the user whose role is being looked up is not the
            // caller. The real Services.User is reached by tokens that do carry user claims.
            //
            // So the claim connector is structurally correct here and produces nothing on the login
            // path. That is not a bug being hidden: test-phase12.ps1 proves the connector works by
            // calling the same endpoint with a token that DOES carry the claim
            // (userservice-claimprobe-svc), which is the only way to separate "the cascade never
            // reached position 2" from "it reached it and found nothing."
            logger.LogInformation(
                "Claim connector for tenant {TenantKey} / {HandlerName}: no '{ClaimName}' claim on the caller's token",
                tenant.Key, handlerName, claimName);
            return ServiceExtensibilityResult<string>.Succeeded(null);
        }

        return ServiceExtensibilityResult<string>.Succeeded(value);
    }
}
