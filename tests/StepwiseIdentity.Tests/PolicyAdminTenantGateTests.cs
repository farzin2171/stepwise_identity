using Mini.AuthorizationService;
using Mini.Infrastructure.Identity;

namespace StepwiseIdentity.Tests;

// Phase 23's one branching decision: does the caller's own tenant match the route's tenantKey on
// Mini.AuthorizationService's PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}? Table-
// tested directly, the same way WebhookSubscriptionMatcherTests.cs tests "which tenant does this route
// to" rather than only through test-phase23.ps1 end to end.
public class PolicyAdminTenantGateTests
{
    // A User identity is rejected the moment its own tenant differs from the route - this is the gap
    // Phase 21 left open and Phase 23 closes.
    [Theory]
    [InlineData("acme", "acme", true)]
    [InlineData("globex", "acme", false)]
    [InlineData("acme", "globex", false)]
    // A User identity with no tenant at all (a token whose client never requested "tenant" - see
    // IdentityContextTests.cs) never matches any real route tenant, so it's also denied.
    [InlineData(null, "acme", false)]
    public void AUserMustMatchTheRouteTenant(string? callerTenantKey, string routeTenantKey, bool expected)
    {
        var allowed = PolicyAdminTenantGate.IsAllowed(IdentityType.User, callerTenantKey, routeTenantKey);

        Assert.Equal(expected, allowed);
    }

    // A Service identity is deliberately exempt, regardless of its own tenant (parsed from the
    // client_id suffix, if any) - see this class's own header comment and CONTEXT.md's
    // "Service account" entry for why: this repo already has tenant-less service callers by design.
    [Theory]
    [InlineData("acme", "globex")]
    [InlineData(null, "acme")]
    [InlineData("globex", "globex")]
    public void AServiceIdentityIsAlwaysExempt(string? callerTenantKey, string routeTenantKey)
    {
        var allowed = PolicyAdminTenantGate.IsAllowed(IdentityType.Service, callerTenantKey, routeTenantKey);

        Assert.True(allowed);
    }
}
