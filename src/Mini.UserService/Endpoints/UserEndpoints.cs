using Microsoft.EntityFrameworkCore;
using Mini.UserService.Connectors;
using Mini.UserService.Connectors.Handlers;
using Mini.UserService.Data;
using Mini.UserService.ExternalServices;

namespace Mini.UserService.Endpoints;

// Real counterpart: Services.User's UserController and UserIdentityController, both under /api/v2.
// The three routes ported here are the three that matter to this course:
//
//   GET /user/identities/role/{userId}          the role claim the IdG enriches every token with
//   GET /user/identities/email?email=…          resolve a user by email (Phase 12)
//   GET /useridentity/convert/{userId}          local <-> external id conversion, which calls BACK
//                                               into the IdG
//
// The third one is the reason Phase 11 exists in the shape it does. Until then every arrow in this
// repo pointed one way. Services.User genuinely calls the Identity Gateway (IIdentityClientV1,
// GET /user/convert/{userId}?convertTo=...) while the Identity Gateway calls Services.User for the
// role claim — a real, bidirectional dependency between two services, each authenticating to the other
// by a DIFFERENT mechanism. See docs/architecture/service-to-service-auth.md.
//
// Phase 12 changed the first one and added the second. Both now go through the connector machinery in
// Connectors/ when the caller names a tenant: the role no longer comes from one table for everybody,
// it comes from whatever source that tenant's rows say it comes from. See
// docs/architecture/connectors.md.
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var user = app.MapGroup("/api/v2").RequireAuthorization("UserApi");

        // Route shape fixed by IdentityServerHost/ExternalServices/UserClient.cs, which builds
        // "{Address}/v2/User/identities/role/{subjectId}" and reads the response as a raw string.
        // Results.Text, not Results.Ok — Ok would JSON-encode it to "Admin" with quotes, and the
        // caller does no deserialization at all.
        //
        // Phase 12 added the OPTIONAL tenant query parameter, and optional is the load-bearing word.
        // Connector configuration is per-tenant, so a connector-driven lookup cannot happen without
        // knowing which tenant — and the caller is the only party who knows, for the same reason the
        // conversion endpoint below has to be told: the IdG reaches this service with a self-issued
        // JWT that carries no tenant claim and no client_id suffix to parse one out of (CONTEXT.md,
        // "Self-issued JWT"). Leaving it out is not an error, it is the pre-Phase-12 path, which is
        // what lets test-phase7.ps1 and test-phase11.ps1 keep exercising the local table unchanged.
        user.MapGet("/User/identities/role/{userId}", async (
            string userId,
            string? tenant,
            ServiceDbContext db,
            GetUserRoleHandler handler,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(tenant))
            {
                return Results.Text(await GetLocalRoleAsync(db, userId, ct));
            }

            var connectorTenant = await ResolveConnectorTenantAsync(db, tenant, ct);
            if (connectorTenant is null)
            {
                // An unknown or deactivated tenant key. 404 rather than falling back to the local
                // table, because "I don't know that tenant" and "that tenant has no connectors" are
                // different answers and collapsing them would hide a typo in the caller's config as a
                // plausible-looking role.
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Unknown tenant",
                    detail: $"No active tenant with key '{tenant}'.");
            }

            var result = await handler.ExecuteAsync(connectorTenant, userId, ct);

            return result.Outcome switch
            {
                // A connector answered. This is Acme's own web API for alice and carol.
                ConnectorChainOutcome.Answered => Results.Text(result.Value!),

                // The chain ran to the end and nobody knew this user. "Member" is the same fallback
                // the local path uses, and applying it here rather than reading the table is
                // deliberate: a tenant that has opted into connectors is served by its own sources,
                // full stop. The local table is not a last resort underneath the chain — it is what a
                // tenant gets when it never opted in. Anything else would mean a tenant's own system
                // saying "this person left" gets silently overridden by a stale row in MiniUsers.
                ConnectorChainOutcome.NoValue => Results.Text("Member"),

                // No enabled row in either choice table — Globex, whose AzureGraph pick is disabled at
                // the base level. Exactly the Phase 11 behaviour, which is what makes Globex the
                // control case for this phase.
                ConnectorChainOutcome.NoConnectorConfigured => Results.Text(await GetLocalRoleAsync(db, userId, ct)),

                // A single, non-cascading connector could not answer. 502: this service is fine, the
                // tenant's own integration is not, and saying so is more useful than a 500 that
                // implicates the wrong system. Not reachable for the role handler with the seeded
                // rows (both cascading tenants absorb failures) — it is reachable the moment somebody
                // gives a tenant a non-cascading role connector, which is the point of handling it.
                _ => Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Connector failed",
                    detail: string.Join(" | ", result.Errors))
            };
        });

        // Phase 12. Real counterpart: Services.User's GET /user/identities/email?email=… .
        //
        // Nothing in this repo calls it — no login needs it. It exists because it is the one handler
        // that is NOT cascading for any tenant, and therefore the only place a connector failure is
        // allowed to be fatal. Without it, every failure in this sample would be quietly absorbed by a
        // chain and the difference between the two choice tables would be undemonstrable.
        // Both parameters are declared NULLABLE even though both are required, and that is not
        // sloppiness — it is the only way to get a usable error out of this endpoint. Declared as
        // non-nullable `string`, a missing query parameter makes minimal-API parameter binding throw
        // BadHttpRequestException before the handler body runs at all, and app.UseExceptionHandler()
        // then reports it as a 500 rather than honouring its 400. So the caller gets "something
        // exploded in the user service" for what is actually "you forgot ?tenant=". Binding them as
        // nullable and validating here puts the 400 (and a ProblemDetails saying which parameter and
        // why) back in reach. See the README's Phase 12 "things that broke" #3.
        user.MapGet("/User/identities/email", async (
            string? email,
            string? tenant,
            ServiceDbContext db,
            GetUserByEmailHandler handler,
            CancellationToken ct) =>
        {
            // tenant is required here, unlike the role route. There is no local table of email
            // addresses to fall back to, so there is no pre-connector behaviour to preserve.
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(tenant))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing parameter",
                    detail: "Both 'email' and 'tenant' are required — an email lookup has no source without a tenant.");
            }

            var connectorTenant = await ResolveConnectorTenantAsync(db, tenant, ct);
            if (connectorTenant is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Unknown tenant",
                    detail: $"No active tenant with key '{tenant}'.");
            }

            var result = await handler.ExecuteAsync(connectorTenant, email, ct);

            return result.Outcome switch
            {
                ConnectorChainOutcome.Answered => Results.Ok(result.Value),

                // The tenant's system answered "no such user."
                ConnectorChainOutcome.NoValue => Results.NotFound(),

                // Globex. The same outcome the role route turns into a local-table read means
                // something different here: there is no source at all. 501, not 404 — a 404 would say
                // "no such user," and the truth is that nobody was asked.
                ConnectorChainOutcome.NoConnectorConfigured => Results.Problem(
                    statusCode: StatusCodes.Status501NotImplemented,
                    title: "No user source configured",
                    detail: $"Tenant '{tenant}' has no enabled connector for '{HandlerNames.GetUserByEmail}'."),

                // Initech, whose WebApiConnectorConfiguration.Host names Acme's API, which refuses a
                // token that is not Acme's. Non-cascading, so nothing absorbs it and the caller sees
                // it — the exact same misconfiguration that the role route's cascade hides.
                _ => Results.Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Connector failed",
                    detail: string.Join(" | ", result.Errors))
            };
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

    // The pre-Phase-12 lookup, unchanged and still reachable — this is what a tenant with no connector
    // rows gets, and what a caller that names no tenant gets.
    //
    // "Member" is a FALLBACK, not a row. Every subject gets a role whether or not anyone assigned one,
    // which is why this never 404s while the tenant lookup always can. The real UserClient has a
    // second, sharper version of the same idea: it short-circuits to "Guest" without calling out at
    // all for guest users. This sample has no guest concept, so the fallback lives at the callee.
    private static async Task<string> GetLocalRoleAsync(ServiceDbContext db, string userId, CancellationToken ct)
    {
        var role = await db.UserIdentityRoles
            .Where(r => r.UserId == userId)
            .Select(r => r.Role)
            .FirstOrDefaultAsync(ct);

        return role ?? "Member";
    }

    // The extra query Phase 12 forced, and it is worth knowing why it cannot be a join.
    //
    // Connector configuration is keyed on the tenant GUID (the real tables are, and
    // Connectors/Data/CascadingConnectorDbContext.cs keeps that), while the caller names a tenant by
    // its friendly key and Mini.Infrastructure's TokenClient needs that key to pick the right
    // per-tenant secret. The GUID lives in ServiceDbContext's Tenants table; the connector rows live
    // in a DIFFERENT DbContext over the same database. EF cannot join across two contexts, so this is
    // a separate round trip by construction, not by oversight.
    //
    // Filters on IsActive, matching TenantEndpoints — so deactivating a tenant takes its connectors
    // out of service too, rather than leaving them resolvable through a side door.
    private static async Task<ConnectorTenant?> ResolveConnectorTenantAsync(
        ServiceDbContext db,
        string tenantKey,
        CancellationToken ct)
    {
        var tenant = await db.Tenants
            .Where(t => t.Key == tenantKey && t.IsActive)
            .Select(t => new { t.TenantId, t.Key })
            .FirstOrDefaultAsync(ct);

        return tenant is null ? null : new ConnectorTenant(tenant.TenantId, tenant.Key);
    }
}
