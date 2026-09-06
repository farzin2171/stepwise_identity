using Microsoft.AspNetCore.Http;

namespace Mini.Infrastructure.Identity;

// Port of Services.Authorization's IdentityPrincipalMiddleware (Libraries.Infrastructure/DIT.Identity),
// minus the "on-behalf-of" header merge — this sample has no agent-acting-for-client scenario. Must run
// after UseAuthentication() (needs ctx.User populated) and before UseAuthorization() (policies and
// filters further down the pipeline can then rely on IIdentityContext instead of raw claims lookups).
public class IdentityContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IIdentityContext identityContext)
    {
        identityContext.Populate(context.User);
        await next(context);
    }
}
