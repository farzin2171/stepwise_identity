using Microsoft.EntityFrameworkCore;
using Mini.UserService.Data;

namespace Mini.UserService.Endpoints;

// Real counterpart: Services.TenantManagement's read API, /api/v1/tenants — a genuinely SEPARATE
// service from Services.User in production, with its own repo, its own database and its own JWT
// audience ("tenantmgntapi"). This sample collapses the two into one process, exactly as
// ExternalServicesStub did, because the alternative is a Mini.TenantService that would teach nothing
// Mini.UserService doesn't already teach.
//
// What survives the collapse is the AUDIENCE. This group requires "tenantmgntapi" and
// UserEndpoints requires "userapi", so the two surfaces stay separately addressable: a caller holding
// a User-service token cannot read the tenant registry with it. That's the one property of the real
// two-service split worth keeping, and it costs one authorization policy — see Program.cs.
public static class TenantEndpoints
{
    public static void MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        // Route shape is fixed by the existing caller, not chosen here:
        // IdentityServerHost/ExternalServices/TenantClient.cs builds
        // "{Address}/v1/tenants/GetByKey/{tenantKey}" and reads a { tenantId } body out of the
        // response. Phase 11 replaces what's BEHIND this route, not the route — which is why
        // test-phase7.ps1 needs no edit to start exercising a real service with a real database.
        var tenants = app.MapGroup("/api/v1/tenants").RequireAuthorization("TenantApi");

        tenants.MapGet("/GetByKey/{tenantKey}", async (string tenantKey, ServiceDbContext db, CancellationToken ct) =>
        {
            // Filters on IsActive. The real service's own analysis flags it as unconfirmed whether its
            // read API does — its management API hard-deletes while its read API soft-deletes, which
            // strongly suggests reads are meant to hide soft-deleted rows, but "strongly suggests" is
            // not "verified." Filtering here makes the sample's choice explicit and, more usefully,
            // makes deactivation observable: flip IsActive to false and the login that depended on
            // this lookup starts failing during token issuance.
            // The .ToString() is deliberately OUTSIDE the query. Written as
            // .Select(t => new { tenantId = t.TenantId.ToString(), ... }) it gets translated into SQL
            // and executed by SQL Server as CONVERT(char(36), ...), which returns the GUID in
            // UPPERCASE — where .NET's Guid.ToString() returns lowercase. The stub returned lowercase
            // string literals, so that translation silently changed the tenant_guid claim's case for
            // every token this sample issues. Projecting the Guid and formatting it in memory keeps
            // .NET's rules. See the README's Phase 11 "things that broke."
            var tenant = await db.Tenants
                .Where(t => t.Key == tenantKey && t.IsActive)
                .Select(t => new { t.TenantId, t.Key, t.Name })
                .FirstOrDefaultAsync(ct);

            // 404, not an empty 200. TenantClient calls EnsureSuccessStatusCode(), so a missing tenant
            // surfaces as an exception during token issuance rather than a token with a blank
            // tenant_guid claim — the Phase 9 failure mode described in ExternalServicesStub's own
            // comments, preserved here deliberately.
            return tenant is null
                ? Results.NotFound()
                : Results.Ok(new { tenantId = tenant.TenantId.ToString(), key = tenant.Key, name = tenant.Name });
        });

        // No caching, on purpose, and this is the interesting half of the never-expiring-cache lesson.
        // The real Services.TenantManagement registers a MemoryCache and never applies it — every IdG
        // lookup hits SQL. Meanwhile the IdG caches the ANSWER forever
        // (SampleProfileService.GetCachedTenantGuidAsync, AbsoluteExpiration = DateTimeOffset.MaxValue).
        // So the service that owns the data has no cache to invalidate, and the caller that cached it
        // has no expiry: there is no point in the system where a changed tenant GUID can propagate.
        // Adding a cache here would not help. That asymmetry is why the bug is a caller-side bug.
    }
}
