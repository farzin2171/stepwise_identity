namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: Infrastructure/MultiTenant/MultiTenantMiddleware.cs — but a genuinely different
// SHAPE, not just a simplification, and worth being explicit about why. The real MultiTenantMiddleware
// resolves tenant from the REQUEST itself (the hostname for a browser request, a JWT claim for an API
// request) — it works before anyone has necessarily logged in, because Apply's tenant is a property of
// which DOMAIN you're visiting. This sample deliberately kept the earlier phases' explicit
// "LoginAsTenant" button flow instead of hostname-based routing (see this project's README), which means
// there IS no tenant to resolve before login — the tenant only becomes known once IdentityServerHost
// hands back a "tenant_id" claim on the authenticated user. So this middleware resolves tenant the way
// Apply's OWN code resolves it for API requests (from a JWT claim), applied here to every request instead
// of just API ones, because in THIS sample that's the only resolution source that exists at all.
//
// Real hostname-based resolution — and the cross-check that catches a user authenticated for tenant A
// showing up under tenant B's hostname — is a documented next step, not implemented here. See this
// project's README for what that would take.
public class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantKey = context.User.FindFirst("tenant_id")?.Value;
            var tenant = Tenants.Find(tenantKey);
            if (tenant is not null)
            {
                tenantContext.SetTenant(tenant);
            }
        }

        await next(context);
    }
}
