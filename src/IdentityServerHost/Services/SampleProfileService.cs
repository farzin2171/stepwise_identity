using System.Security.Claims;
using Duende.IdentityServer;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using IdentityServerHost.ExternalServices;
using Microsoft.Extensions.Caching.Distributed;

namespace IdentityServerHost.Services;

// Replaces the default TestUser-backed profile service, which only knows how to answer "is this user
// active?" for subjects it finds in TestUsers.Users. That default silently rejects the
// externally-provisioned principal ExternalController.cs signs in ("User is not active" in the log,
// nothing more specific) — subjects like "external:external-idp:ext-1" were never going to be in that
// list. IdG counterpart: EquisoftProfileService, which answers the same question against the real
// UserStore instead of a hard-coded list either way.
//
// Phase 7 also made this the integration point for TenantClient/UserClient — the real system does the
// equivalent stamping in a custom ITokenResponseGenerator at token-issuance time; this sample already
// had a component that assembles claims at token-issuance time (this one), so that's where it went
// instead of adding a second, parallel component for the same job.
public class SampleProfileService(
    TestUserStore testUsers,
    ExternalUserStore externalUsers,
    TenantClient tenantClient,
    UserClient userClient,
    IDistributedCache cache) : IProfileService
{
    public async Task GetProfileDataAsync(ProfileDataRequestContext context, CancellationToken ct = default)
    {
        var subjectId = context.Subject.GetSubjectId();
        var claims = IsLocalUser(context.Subject)
            ? testUsers.FindBySubjectId(subjectId)?.Claims
            : await externalUsers.FindAsync(subjectId);

        if (claims is null)
        {
            return;
        }

        var enrichedClaims = claims.ToList();

        // tenant_guid is ADDED alongside the existing tenant_id claim (Phase 3), not a replacement for
        // it — the real IdG's tenant_id IS this GUID, but MvcClient's ITenantContext and SampleApi's
        // IIdentityContext both already resolve tenant FROM tenant_id's friendly-key shape (see
        // CONTEXT.md's TenantClient/UserClient entry). Changing what tenant_id itself contains would
        // ripple into both of those, for a phase that's only about proving this HTTP-call pattern out.
        var tenantKey = enrichedClaims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        if (tenantKey is not null)
        {
            enrichedClaims.Add(new Claim("tenant_guid", await GetCachedTenantGuidAsync(tenantKey, ct)));
        }

        // Deliberately NOT cached — the direct contrast to tenant_guid above, and to the real system's
        // own UserClient, which is also never cached. See UserClient.cs.
        enrichedClaims.Add(new Claim("role", await userClient.GetRoleAsync(subjectId, ct)));

        context.AddRequestedClaims(enrichedClaims);
    }

    // Real IdG counterpart: EquisoftTokenResponseGenerator.AddTenantIdToPayloadAsync, caching in
    // IDistributedCache with AbsoluteExpiration = DateTimeOffset.MaxValue. That's a real bug there (a
    // tenant's GUID changing at the source of truth is never picked up without an app restart) —
    // reproduced here on purpose, not quietly fixed. Phase 3's README named this exact caveat before
    // this course had the code to reproduce it.
    private async Task<string> GetCachedTenantGuidAsync(string tenantKey, CancellationToken ct)
    {
        var cacheKey = $"tenant_id_from_key_{tenantKey}";
        var cached = await cache.GetStringAsync(cacheKey, ct);
        if (cached is not null)
        {
            return cached;
        }

        var tenantGuid = await tenantClient.GetTenantAsync(tenantKey, ct);
        await cache.SetStringAsync(cacheKey, tenantGuid, new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = DateTimeOffset.MaxValue
        }, ct);
        return tenantGuid;
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
