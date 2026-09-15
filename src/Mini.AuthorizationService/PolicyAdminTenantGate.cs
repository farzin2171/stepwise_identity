using Mini.Infrastructure.Identity;

namespace Mini.AuthorizationService;

// Phase 23: the one branching decision the policy-admin PUT endpoint's tenant-match fix adds, pulled
// out into its own pure, static, table-testable method rather than left inline in Program.cs's minimal
// API lambda - the same reasoning WebhookSubscriptionMatcher.Matches (Mini.MessageCenter, Phase 20)
// already applies to "which tenant does this route to."
//
// A User identity must match the route's tenantKey - that's the whole gap Phase 21 left open. A
// Service identity is deliberately exempt: this repo already has tenant-less service callers by design
// (see CONTEXT.md's "Service account" entry on userservice-mgmt-svc), and this endpoint has no genuine
// service caller today whose own cross-tenant need would justify narrowing the exemption further.
public static class PolicyAdminTenantGate
{
    public static bool IsAllowed(IdentityType identityType, string? callerTenantKey, string routeTenantKey)
    {
        if (identityType != IdentityType.User)
        {
            return true;
        }

        return callerTenantKey == routeTenantKey;
    }
}
