using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Mini.AuthorizationService;
using Mini.AuthorizationService.Data;
using Mini.Infrastructure.Identity;
using Mini.Infrastructure.Messaging;

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

// Phase 21: this service's first real use of the bus as a PUBLISHER rather than a consumer.
// Mini.MessageCenter (Phase 20) called AddMessageBus with a configureConsumers callback that
// registers a consumer; here the callback is omitted entirely, which AddMessageBus already
// supports (configureConsumers is optional - see Mini.Infrastructure/Messaging/MessageBusExtensions.cs).
// No new registration was needed to make Phase 19's port serve a publish-only caller.
builder.Services.AddMessageBus(builder.Configuration);
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
        // Phase 22: Condition joined the projection. Nothing needed it through Phase 21 — every
        // existing caller (test-phase13.ps1 through test-phase21.ps1) only ever asked "does a policy
        // for this resource exist," never "what does it currently say." AgentPortal's new policy-edit
        // page (PolicyController.Edit) is the first caller that needs to show the CURRENT Condition
        // before letting an agent replace it, so this is the "add a minimal GET if genuinely missing"
        // case the prompt called for — done by widening the existing endpoint's shape rather than
        // adding a near-duplicate one.
        .Select(p => new { p.Id, p.Name, p.ResourceName, p.Description, p.IsEnabled, p.Order, p.Condition })
        .ToList();

    return Results.Ok(policies);
});

// Phase 21: the policy-admin API. Upserts a Policy row's Condition, invalidates any cached decision
// that depended on it, and publishes a real PolicyChangedEvent - the first production call to
// Publish anywhere in this repo (Mini.MessageCenter's diagnostic endpoint from Phase 20 was the
// only caller before this). Deliberately open to any authenticated caller, not gated to service
// accounts or a specific role (design decision, see this project's README "Where this sample
// simplifies") - the same open-editing posture SampleApi's IIdentityContext already documents as a
// gap (two callers with different privilege look identical to a claims-only check).
api.MapPut("/policies/{tenantKey}/{resourceName}", async (
    string tenantKey,
    string resourceName,
    UpdatePolicyRequest request,
    AuthorizationDbContext db,
    IPublishEndpoint publishEndpoint) =>
{
    var existing = db.Policies.SingleOrDefault(p => p.TenantKey == tenantKey && p.ResourceName == resourceName);
    var oldCondition = existing?.Condition;
    var changedAtUtc = DateTimeOffset.UtcNow;

    if (existing != null)
    {
        existing.Condition = request.Condition;
        if (request.Name != null) existing.Name = request.Name;
        if (request.PolicyType != null) existing.PolicyType = request.PolicyType;
        if (request.Order.HasValue) existing.Order = request.Order.Value;
    }
    else
    {
        // Upsert, not require-exists - the simplest option, and it mirrors how these rows already
        // get created: SeedData just inserts them, there was never a "create" endpoint distinct from
        // "update" to begin with.
        existing = new Policy
        {
            Id = Guid.NewGuid(),
            TenantKey = tenantKey,
            ResourceName = resourceName,
            Name = request.Name ?? $"{tenantKey}-{resourceName}",
            PolicyType = request.PolicyType ?? "Role",
            Condition = request.Condition,
            IsEnabled = true,
            Order = request.Order ?? 1
        };
        db.Policies.Add(existing);
    }

    // Cache invalidation, decided rather than deferred: the README through Phase 15 named "cache
    // eviction on policy change" as deliberately missing, on the theory that the real Redis-backed
    // Services.Authorization cache has the same gap. That reasoning doesn't survive contact with a
    // real write path - now that a real caller can change a Policy, leaving stale CachedDecisions
    // rows in place for up to 30 seconds means "authorized: true" for a policy that was JUST
    // tightened. Reusing the same clear-by-tenant logic the DELETE endpoint below already has, but
    // scoped further to this resource so an update to "sample-api" doesn't also evict "agent-portal"
    // decisions for the same tenant.
    var stale = db.CachedDecisions.Where(c => c.TenantKey == tenantKey && c.ResourceName == resourceName);
    db.CachedDecisions.RemoveRange(stale);

    db.SaveChanges();

    // The real trigger Phase 20's throwaway diagnostic endpoint was standing in for.
    await publishEndpoint.Publish(new PolicyChangedEvent
    {
        TenantKey = tenantKey,
        ResourceName = resourceName,
        OldCondition = oldCondition,
        NewCondition = request.Condition,
        ChangedAtUtc = changedAtUtc
    });

    return Results.Ok(new
    {
        existing.Id,
        existing.TenantKey,
        existing.Name,
        existing.ResourceName,
        existing.PolicyType,
        existing.Condition,
        existing.IsEnabled,
        existing.Order
    });
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
