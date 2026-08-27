using System.Security.Claims;
using Duende.IdentityServer;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;

namespace IdentityServerHost.Services;

// Replaces the default TestUser-backed profile service, which only knows how to answer "is this user
// active?" for subjects it finds in TestUsers.Users. That default silently rejects the
// externally-provisioned principal ExternalController.cs signs in ("User is not active" in the log,
// nothing more specific) — subjects like "external:external-idp:ext-1" were never going to be in that
// list. IdG counterpart: EquisoftProfileService, which answers the same question against the real
// UserStore instead of a hard-coded list either way.
public class SampleProfileService(TestUserStore testUsers, ExternalUserStore externalUsers) : IProfileService
{
    public async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken ct = default)
    {
        var subjectId = context.Subject.GetSubjectId();
        var claims = IsLocalUser(context.Subject)
            ? testUsers.FindBySubjectId(subjectId)?.Claims
            : await externalUsers.FindAsync(subjectId);

        if (claims is not null)
        {
            context.AddRequestedClaims(claims);
        }
    }

    public async Task IsActiveAsync(IsActiveContext context, CancellationToken ct = default)
    {
        var subjectId = context.Subject.GetSubjectId();
        context.IsActive = IsLocalUser(context.Subject)
            ? testUsers.FindBySubjectId(subjectId) is not null
            : await externalUsers.FindAsync(subjectId) is not null;
    }

    private static bool IsLocalUser(ClaimsPrincipal subject) =>
        subject.FindFirst("idp")?.Value == IdentityServerConstants.LocalIdentityProvider;
}
