using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Mini.AuthorizationService;
using Mini.AuthorizationService.Data;
using Mini.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

var authentication = builder.Configuration.GetSection("Authentication");
var connectionString = builder.Configuration.GetConnectionString("MiniAuthorizationDb");

builder.Services.AddDbContext<AuthorizationDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Mini.AuthorizationService")));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authentication["Authority"];
        options.RequireHttpsMetadata = false;
        // Phase 15: accepts both "authapi" (service accounts calling /evaluate directly, e.g.
        // test-phase13.ps1) and "api1" (SampleApi forwarding its caller's own token, unmodified, so
        // /evaluate sees the real caller's identity — not a generic service-to-service credential
        // with no tenant of its own). See "Things that broke" in this project's README.
        options.TokenValidationParameters.ValidAudiences = authentication.GetSection("Audiences").Get<string[]>();
        options.MapInboundClaims = false;
    });

builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IIdentityContext>(sp =>
{
    var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
    var ctx = new Mini.Infrastructure.Identity.IdentityContext();
    ctx.Populate(httpContext?.User ?? new System.Security.Claims.ClaimsPrincipal());
    return ctx;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthorizationDbContext>();
    db.Database.Migrate();
    AuthorizationDbContext.SeedData(db);
}

app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api/v1/authorization").RequireAuthorization();

api.MapPost("/evaluate", (AuthorizationEvaluationRequest request, AuthorizationDbContext db, IIdentityContext identity) =>
{
    var subjectKey = identity.ClientId ?? identity.Subject ?? "anonymous";
    var contextHash = DecisionCache.ComputeContextHash(request.Context);
    var now = DateTime.UtcNow;

    var cached = db.CachedDecisions.SingleOrDefault(c =>
        c.TenantKey == identity.TenantKey && c.SubjectKey == subjectKey &&
        c.ResourceName == request.ResourceName && c.ContextHash == contextHash);

    if (cached != null && !DecisionCache.IsExpired(cached.ExpiresAtUtc, now))
    {
        return Results.Ok(new { authorized = cached.Authorized, reason = cached.Reason, cached = true });
    }

    var policies = db.Policies
        .Where(p => p.TenantKey == identity.TenantKey && p.ResourceName == request.ResourceName && p.IsEnabled)
        .ToList();

    bool authorized = false;
    string reason = "No policies found for this resource";

    if (policies.Count > 0)
    {
        reason = "No applicable policies granted access";
        foreach (var policy in policies.OrderBy(p => p.Order))
        {
            if (policy.EvaluatePolicy(identity, request))
            {
                authorized = true;
                reason = $"Policy '{policy.Name}' granted access";
                break;
            }
        }
    }

    // Cache-aside: overwrite whatever was there (expired row, or none) with the freshly evaluated
    // decision. Persisted to the same database as Policies, so — unlike an in-process
    // Dictionary<TKey,TValue> — a restart of this service doesn't lose it.
    if (cached != null)
    {
        cached.Authorized = authorized;
        cached.Reason = reason;
        cached.CreatedAtUtc = now;
        cached.ExpiresAtUtc = now + DecisionCache.Ttl;
    }
    else
    {
        db.CachedDecisions.Add(new CachedDecision
        {
            Id = Guid.NewGuid(),
            TenantKey = identity.TenantKey ?? "",
            SubjectKey = subjectKey,
            ResourceName = request.ResourceName,
            ContextHash = contextHash,
            Authorized = authorized,
            Reason = reason,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + DecisionCache.Ttl
        });
    }
    db.SaveChanges();

    return Results.Ok(new { authorized, reason, cached = false });
});

api.MapGet("/policies", (AuthorizationDbContext db, IIdentityContext identity) =>
{
    var policies = db.Policies
        .Where(p => p.TenantKey == identity.TenantKey)
        .Select(p => new { p.Id, p.Name, p.ResourceName, p.Description, p.IsEnabled, p.Order })
        .ToList();

    return Results.Ok(policies);
});

// Real cache invalidation, unlike SampleApi's Phase-14 admin/cache endpoint (which had no cache to
// clear). Same gating as that endpoint: service accounts only, via ServiceAccountOnlyFilter, no
// .RequireAuthorization() policy.
api.MapDelete("/cache/{tenantKey}", (string tenantKey, AuthorizationDbContext db) =>
{
    var cleared = db.CachedDecisions.Where(c => c.TenantKey == tenantKey);
    var count = cleared.Count();
    db.CachedDecisions.RemoveRange(cleared);
    db.SaveChanges();

    return Results.Ok(new { message = $"Cleared {count} cached decision(s) for tenant '{tenantKey}'." });
}).AddEndpointFilter<ServiceAccountOnlyFilter>();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
