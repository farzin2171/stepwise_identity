using Microsoft.AspNetCore.Authentication.JwtBearer;

// A stand-in for the real IdG's two sibling DIT microservices (Tenant Management API, User API),
// collapsed into one process — a further simplification on top of the real system's own already-two
// services, made purely for this course's sake. See ../IdentityServerHost/README.md's Phase 7 section
// for the full write-up of what calls this and why.
var builder = WebApplication.CreateBuilder(args);

// Same shape as SampleApi's own JWT Bearer setup — except the "issuer" here is IdentityServerHost
// acting as its OWN client, not a real user's login. IdentityServerHost mints these tokens itself
// (IIdentityServerTools.IssueClientJwtAsync), signed with the same key it signs every other token
// with, so validating them here needs nothing beyond IdentityServerHost's own discovery document.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           options.Authority = "https://localhost:5001";
           options.TokenValidationParameters.ValidAudiences = ["tenantmgntapi", "userapi"];
       });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Stands in for the real Tenant Management service's own database. IdentityServerHost's own
// Tenants.cs (Phase 3) keeps a separate, independent copy of these same keys — two systems agreeing
// by convention, not by sharing a table, exactly like the real IdG and Apply's own tenant registries
// (see MvcClient's docs/multitenancy-and-external-services.md for that same point made about Apply).
var tenantsByKey = new Dictionary<string, string>
{
    ["acme"] = "8f14e45f-ceea-467e-bd42-05d1a4a6b3f0",
    ["globex"] = "c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e",
    // Phase 9. Initech had to be added HERE as well as in IdentityServerHost's Tenants.cs, and the fact
    // that it's two edits in two processes is the point the comment above is making: these registries
    // agree by convention, not by sharing a table. Miss this one and the login itself succeeds — the
    // failure surfaces later and elsewhere, as a 404 out of TenantClient during token issuance, which
    // reads like a broken external service rather than a missing row.
    ["initech"] = "a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93"
};

app.MapGet("/api/v1/tenants/GetByKey/{key}", (string key) =>
       tenantsByKey.TryGetValue(key, out var tenantId)
           ? Results.Ok(new { tenantId })
           : Results.NotFound())
   .RequireAuthorization();

// Stands in for the real User service's role lookup — a fixed table, not a real permission system.
// Keyed by whatever subject id IdentityServerHost passes in (its own local subject ids, "1"/"2" for
// alice/bob — the real method signature says "externalUserId," but this sample calls it for every
// login, local or federated, see SampleProfileService.cs).
var rolesBySubjectId = new Dictionary<string, string>
{
    ["1"] = "Admin" // alice
};

app.MapGet("/api/v2/User/identities/role/{subjectId}", (string subjectId) =>
       Results.Text(rolesBySubjectId.GetValueOrDefault(subjectId, "Member")))
   .RequireAuthorization();

// Phase 10: an unauthenticated liveness probe, so run-all.ps1 can tell "this process is listening and
// finished starting" from "this port is open but the app is still warming up." Deliberately the plainest
// thing that answers that question — the real services use DIT.HealthChecks, which additionally reports
// on each dependency (database, downstream service) and is what an orchestrator scrapes.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
