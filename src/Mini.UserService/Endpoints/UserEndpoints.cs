using Microsoft.EntityFrameworkCore;
using Mini.UserService.Data;
using Mini.UserService.ExternalServices;

namespace Mini.UserService.Endpoints;

// Real counterpart: Services.User's UserController and UserIdentityController, both under /api/v2.
// The two routes ported here are the two that matter to this course:
//
//   GET /user/identities/role/{userId}          the role claim the IdG enriches every token with
//   GET /useridentity/convert/{userId}          local <-> external id conversion, which calls BACK
//                                               into the IdG
//
// The second one is the reason this phase exists in the shape it does. Until now every arrow in this
// repo pointed one way. Services.User genuinely calls the Identity Gateway (IIdentityClientV1,
// GET /user/convert/{userId}?convertTo=...) while the Identity Gateway calls Services.User for the
// role claim — a real, bidirectional dependency between two services, each authenticating to the other
// by a DIFFERENT mechanism. See docs/architecture/service-to-service-auth.md.
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var user = app.MapGroup("/api/v2").RequireAuthorization("UserApi");

        // Route shape fixed by IdentityServerHost/ExternalServices/UserClient.cs, which builds
        // "{Address}/v2/User/identities/role/{subjectId}" and reads the response as a raw string.
        // Results.Text, not Results.Ok — Ok would JSON-encode it to "Admin" with quotes, and the
        // caller does no deserialization at all.
        user.MapGet("/User/identities/role/{userId}", async (string userId, ServiceDbContext db, CancellationToken ct) =>
        {
            var role = await db.UserIdentityRoles
                .Where(r => r.UserId == userId)
                .Select(r => r.Role)
                .FirstOrDefaultAsync(ct);

            // "Member" is a FALLBACK, not a row. Every subject gets a role whether or not anyone
            // assigned one, which is why this endpoint never 404s while the tenant lookup above
            // always can. The real UserClient has a second, sharper version of this same idea: it
            // short-circuits to "Guest" without calling out at all for guest users. This sample has
            // no guest concept, so the fallback lives at the callee instead of the caller.
            return Results.Text(role ?? "Member");
        });

        // Real counterpart: UserIdentityController's GET /useridentity/convert/{userId}?conversionType=
        // {Local|External}. This is the endpoint that makes User -> IdG an outbound call: the mapping
        // between a local subject id and an external provider's subject id lives in the IdG's own
        // UserDbContext (ExternalUserStore, Phase 5), not in this service's database. So this service
        // has the API surface and none of the data, and has to ask.
        //
        // tenantKey is an explicit input rather than being read off the caller's identity, and that is
        // not a shortcut — it is forced. The caller here is the IdG's self-issued JWT, which carries no
        // tenant claim and no client_id suffix to parse one out of (see
        // Mini.Infrastructure/Identity/IdentityContext.cs for the suffix convention it would need). The
        // service-account secret used for the OUTBOUND call is per-tenant, so somebody has to say which
        // tenant, and the only party who knows is the caller.
        user.MapGet("/useridentity/convert/{userId}", async (
            string userId,
            string conversionType,
            string tenant,
            IdentityGatewayClient identityGateway,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<ConversionType>(conversionType, ignoreCase: true, out var parsed))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Unsupported conversionType",
                    detail: $"'{conversionType}' is not a conversion type — expected 'Local' or 'External'.");
            }

            var converted = await identityGateway.ConvertUserIdAsync(userId, parsed, tenant, ct);

            return converted is null
                ? Results.NotFound()
                : Results.Ok(new { userId, conversionType = parsed.ToString(), convertedUserId = converted });
        });
    }
}
