using System.Security.Claims;

namespace SampleApi.Infrastructure.Identity;

// Port of Services.Authorization's IIdentityContext (Libraries.Infrastructure/DIT.Identity) — a
// scoped, request-lifetime view of "who is calling right now," derived once from the validated
// token's claims instead of every endpoint re-reading ClaimsPrincipal by hand.
public interface IIdentityContext
{
    bool IsAuthenticated { get; }
    IdentityType IdentityType { get; }
    string? Subject { get; }
    string? ClientId { get; }
    string? TenantKey { get; }

    void Populate(ClaimsPrincipal principal);
}
