using AgentPortal.Data;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mini.Infrastructure.MultiTenant;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;

namespace AgentPortal.Controllers;

// Phase 22. Gives Agent Portal's new database a reason to exist: viewing and editing a policy's
// Condition for the signed-in agent's OWN tenant, and logging every attempt.
//
// Scope decision (Q7 in the phase brief): this controller only ever reads/writes the "agent-portal"
// resource — the same one HomeController.CheckAuthorization() already evaluates against (Phase 18) —
// scoped to whatever tenant ITenantContext resolves for the signed-in user. It deliberately does NOT
// let an agent pick an arbitrary resource (e.g. "sample-api") or an arbitrary tenant: that would be a
// real feature (cross-resource policy administration) this phase wasn't asked to build, and it would
// blur the one thing this phase IS meant to demonstrate — see the tenant-match gap note below.
//
// The gap this controller exercises rather than works around: Mini.AuthorizationService's PUT endpoint
// (Phase 21) never checks that the caller's own tenant matches the {tenantKey} in the URL — a
// documented, unfixed gap. This controller always sends tenantContext.Tenant.Key (never a value the
// user can type), so IT never triggers the gap — but it's now a REAL, reachable, user-editable path
// that would, if this code (or a future caller) sent a different tenantKey with the same access token.
// Nothing here defends against that; nothing here needed to, to demonstrate that the gap is now live.
[Authorize]
[RequireTenant]
public class PolicyController(
    IPolicyAdminClient policyAdminClient,
    ITenantContext tenantContext,
    AgentPortalDbContext db) : Controller
{
    // The one resource this UI edits — see the scope decision above.
    private const string ResourceName = "agent-portal";

    public async Task<IActionResult> Edit()
    {
        var tenant = tenantContext.Tenant!; // [RequireTenant] guarantees this is non-null here.
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (accessToken is null)
        {
            return View(new PolicyEditViewModel(tenant.Key, ResourceName, null, "No access token found on the current session — sign out and back in."));
        }

        var policy = await policyAdminClient.GetPolicyAsync(ResourceName, accessToken);
        return View(new PolicyEditViewModel(tenant.Key, ResourceName, policy?.Condition, null));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string newCondition)
    {
        var tenant = tenantContext.Tenant!;
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        var subjectId = User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value ?? "unknown";

        if (accessToken is null)
        {
            return View(new PolicyEditViewModel(tenant.Key, ResourceName, newCondition, "No access token found on the current session — sign out and back in."));
        }

        // Read "before" through the SAME client the edit page used to show it, immediately before the
        // write — not cached from the GET action, since an agent could leave the edit page open for a
        // while before submitting.
        var before = await policyAdminClient.GetPolicyAsync(ResourceName, accessToken);

        var outcome = await policyAdminClient.UpdatePolicyAsync(tenant.Key, ResourceName, newCondition, accessToken);

        // Recorded either way — a failed call to Mini.AuthorizationService is exactly the kind of thing
        // an audit trail exists to show, not something to leave unlogged. See PolicyChangeRequest.
        db.PolicyChangeRequests.Add(new PolicyChangeRequest
        {
            Id = Guid.NewGuid(),
            AgentSubjectId = subjectId,
            TenantKey = tenant.Key,
            ResourceName = ResourceName,
            OldCondition = before?.Condition,
            NewCondition = newCondition,
            RequestedAtUtc = DateTime.UtcNow,
            Outcome = outcome.Success ? "Succeeded" : "Failed",
            FailureDetail = outcome.FailureDetail
        });
        await db.SaveChangesAsync();

        if (!outcome.Success)
        {
            return View(new PolicyEditViewModel(tenant.Key, ResourceName, newCondition, $"Update failed: {outcome.FailureDetail}"));
        }

        return RedirectToAction(nameof(History));
    }

    public async Task<IActionResult> History()
    {
        var tenant = tenantContext.Tenant!;
        var rows = await db.PolicyChangeRequests
            .Where(r => r.TenantKey == tenant.Key)
            .OrderByDescending(r => r.RequestedAtUtc)
            .ToListAsync();

        return View(rows);
    }
}

public record PolicyEditViewModel(string TenantKey, string ResourceName, string? CurrentCondition, string? Error);
