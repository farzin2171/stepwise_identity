using Microsoft.EntityFrameworkCore;
using Mini.Infrastructure.Identity;
using Mini.UserService.Data;

namespace Mini.UserService.Endpoints;

// Real counterpart: two management APIs, one per collapsed service —
// Services.TenantManagement's /api/v1/management (create/delete a tenant, gated on the
// "service_isService" claim, audited as CREATE_TENANT / DELETE_TENANT) and Services.User's
// /management ConnectorManagementController (import base/tenant connector configuration,
// service-account gated).
//
// These are the endpoints a real service-account token is actually FOR. The read endpoints above are
// called by the IdG with a self-issued JWT because that is what the real IdG does (Phase 7 verified
// it). Nothing in the real system uses a self-issued JWT to mutate another service's data — that path
// goes through /connect/token with a registered client and a secret, so it can be revoked, rotated and
// audited per caller. Phase 10 predicted this phase would REPLACE the self-issued JWT; it doesn't.
// Both mechanisms exist because the real system has both, for different jobs.
// See docs/architecture/service-to-service-auth.md.
public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        // Each management surface sits behind the same audience as the read surface it manages, so the
        // two collapsed services stay separately addressable here too — a token minted for the User
        // service can't create a tenant.
        //
        // The "...Management" policies additionally require the matching SCOPE, which the read
        // policies don't. ServiceAccountOnlyFilter below is necessary but NOT sufficient on its own:
        // the IdG's self-issued read JWT also has no "sub" and so also reads as a service account. The
        // scope requirement is what actually separates "a registered client acquired a credential
        // through /connect/token" from "a host signed something for itself." See Program.cs's
        // authorization block for the full write-up and how it was verified.
        var tenantManagement = app.MapGroup("/api/v1/management").RequireAuthorization("TenantApiManagement");
        var userManagement = app.MapGroup("/api/v2/management").RequireAuthorization("UserApiManagement");

        // The payoff for Phase 10's "try it yourself": onboarding a tenant no longer means editing a
        // Dictionary literal and restarting a process. It's one authenticated POST. Note what this does
        // NOT fix — IdentityServerHost/Tenants.cs and MvcClient's own Tenants.cs are still hardcoded,
        // so this makes ONE of the three registries in this repo runtime-writable and leaves the drift
        // between them fully intact. See docs/architecture/README.md.
        tenantManagement.MapPost("/tenants", async (
            CreateTenantRequest request,
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid tenant",
                    detail: "Both 'key' and 'name' are required.");
            }

            if (await db.Tenants.AnyAsync(t => t.Key == request.Key, ct))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Tenant already exists",
                    detail: $"A tenant with key '{request.Key}' is already registered.");
            }

            // The real table defaults TenantId with NEWSEQUENTIALID(). CreateVersion7 is the closest
            // managed equivalent — time-ordered, so rows cluster on insert instead of fragmenting the
            // index the way a random v4 GUID primary key does.
            var tenant = new Tenant
            {
                TenantId = Guid.CreateVersion7(),
                Key = request.Key,
                Name = request.Name,
                Description = request.Description,
                IsActive = true,
                CreatedDate = DateTime.UtcNow
            };

            db.Tenants.Add(tenant);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/v1/tenants/GetByKey/{tenant.Key}",
                new { tenantId = tenant.TenantId.ToString(), key = tenant.Key, name = tenant.Name });
        }).AddEndpointFilter<ServiceAccountOnlyFilter>();

        // The real Services.TenantManagement has a genuine split here that its own analysis flags as
        // possibly unintentional: the READ API soft-deletes (sets IsActive = false) while the
        // MANAGEMENT API hard-deletes. This ports the management side, hard-deleting — which is also
        // what lets test-phase11.ps1 clean up the tenant it creates instead of leaving a row behind on
        // every run.
        //
        // Soft-delete is reachable from the read side of this sample too, and is the more interesting
        // experiment: UPDATE Tenants SET IsActive = 0 breaks the *next login* for that tenant during
        // token issuance, because TenantEndpoints filters on IsActive and TenantClient calls
        // EnsureSuccessStatusCode(). See the README's "try it yourself."
        tenantManagement.MapDelete("/tenants/{tenantKey}", async (
            string tenantKey,
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Key == tenantKey, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            db.Tenants.Remove(tenant);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AddEndpointFilter<ServiceAccountOnlyFilter>();

        // Stands in for the real ConnectorManagementController's config import: the same idea (a
        // service account writes reference data this service will later serve to the IdG), one row at a
        // time instead of a whole connector-configuration document.
        userManagement.MapPut("/user/identities/role/{userId}", async (
            string userId,
            AssignRoleRequest request,
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Role))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid role",
                    detail: "'role' is required.");
            }

            var existing = await db.UserIdentityRoles.FirstOrDefaultAsync(r => r.UserId == userId, ct);
            if (existing is null)
            {
                db.UserIdentityRoles.Add(new UserIdentityRole { UserId = userId, Role = request.Role });
            }
            else
            {
                existing.Role = request.Role;
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new { userId, role = request.Role });
        }).AddEndpointFilter<ServiceAccountOnlyFilter>();

        // Removing the ROW is not the same as assigning "Member", and that distinction is why this
        // endpoint exists rather than test-phase11.ps1 just writing the old value back. With no row,
        // UserEndpoints falls back to "Member" — which is what test-phase7.ps1 asserts about bob, and
        // it asserts it precisely because he has no row. Writing "Member" back would leave the
        // assertion passing for a different reason, and a test that passes for the wrong reason has
        // stopped testing anything.
        //
        // Real counterpart: Services.TenantManagement's management API is where its DELETE lives too
        // (a hard delete, next to the read API's soft delete). This one hard-deletes for the same
        // reason: a role row's absence is meaningful here, so there is nothing to soft-delete into.
        userManagement.MapDelete("/user/identities/role/{userId}", async (
            string userId,
            ServiceDbContext db,
            CancellationToken ct) =>
        {
            var existing = await db.UserIdentityRoles.FirstOrDefaultAsync(r => r.UserId == userId, ct);
            if (existing is null)
            {
                return Results.NoContent();
            }

            db.UserIdentityRoles.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).AddEndpointFilter<ServiceAccountOnlyFilter>();
    }
}

public record CreateTenantRequest(string Key, string Name, string? Description);

public record AssignRoleRequest(string Role);
