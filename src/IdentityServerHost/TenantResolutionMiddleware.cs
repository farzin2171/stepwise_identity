using Microsoft.AspNetCore.Http.Extensions;

namespace IdentityServerHost;

// Resolves TenantContext from the acr_values=tenant:<name> convention the real IdG's
// AuthenticationHelper documents in its own log message: "Make sure that the request has the parameter
// 'acr_values' set with property 'tenant:name_of_tenant'". Duende IdentityServer parses this internally
// into AuthorizationRequest.Tenant once an interaction context exists; this middleware reads the raw
// query string instead, so the convention is visible without also requiring
// IIdentityServerInteractionService — a teaching simplification, and the reason this exists as a single
// middleware when the real system has no equivalent single component (see TenantContext.cs).
public class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
    {
        var tenantKey = Tenants.ResolveTenantKey(context.Request.GetEncodedPathAndQuery());
        if (tenantKey is not null)
        {
            tenantContext.TenantKey = tenantKey;
            tenantContext.DisplayName = Tenants.DisplayNames[tenantKey];
        }

        await next(context);
    }
}
