using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: Infrastructure/MultiTenant/TenantIdentificationFilter.cs — but a narrower check than
// the real one, and worth being explicit about the gap. The real filter compares TWO independently
// resolved tenants (host-resolved vs. the tenant/service_tenant claim on the authenticated user) and
// rejects on mismatch — that's the actual security boundary, stopping a user authenticated for Tenant A
// from sliding under Tenant B's hostname. This sample only ever HAS one resolution source (the claim
// itself — see TenantResolutionMiddleware), so there is no independent second source to cross-check
// against; a "mismatch" in the real sense simply can't happen here. What this filter still meaningfully
// checks: that an authenticated user's tenant actually resolved to something in Tenants.All at all. It
// would fail closed (instead of silently proceeding with Tenant = null) if IdentityServerHost's
// "tenant_id" claim value were ever something this app's own tenant registry doesn't recognize — exactly
// the "kept in sync by an ops process, not shared code" divergence Tenants.cs's own comment describes.
public class RequireTenantAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true)
        {
            return; // let [Authorize] (which always runs first) handle anonymous access
        }

        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenantContext.Tenant is null)
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
