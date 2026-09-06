using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Mini.Infrastructure.Identity;

// Port of Services.Authorization's ServiceAccountAuthorizeFilter
// (Equisoft.AuthorizationService/Infrastructure/Authorization). The real one is an MVC
// IAuthorizationFilter — minimal APIs have no controller pipeline, so IEndpointFilter is the direct
// equivalent: it runs in the endpoint's own filter pipeline and can short-circuit before the handler
// runs. Deliberately bypasses the formal authorization-policy system entirely (no [Authorize] / policy
// involved) — same as the original, this filter alone decides both "is there a caller at all" and
// "is that caller a service account."
public class ServiceAccountOnlyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var identityContext = context.HttpContext.RequestServices.GetRequiredService<IIdentityContext>();

        // Results.Unauthorized()/Results.Forbid() would return empty bodies here — AddProblemDetails()
        // only auto-attaches a problem+json body to responses that go through ASP.NET Core's own
        // authorization middleware (a policy's challenge/forbid), not to a raw IResult an endpoint
        // filter returns directly. Results.Problem() gets the same problem+json shape either way.
        if (!identityContext.IsAuthenticated)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        if (identityContext.IdentityType != IdentityType.Service)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
        }

        return await next(context);
    }
}
