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
        options.TokenValidationParameters.ValidAudiences = [authentication["Audience"]!];
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
    var policies = db.Policies
        .Where(p => p.TenantKey == identity.TenantKey && p.ResourceName == request.ResourceName && p.IsEnabled)
        .ToList();

    if (policies.Count == 0)
    {
        return Results.Ok(new { authorized = false, reason = "No policies found for this resource" });
    }

    foreach (var policy in policies.OrderBy(p => p.Order))
    {
        if (policy.EvaluatePolicy(identity, request))
        {
            return Results.Ok(new { authorized = true, reason = $"Policy '{policy.Name}' granted access" });
        }
    }

    return Results.Ok(new { authorized = false, reason = "No applicable policies granted access" });
});

api.MapGet("/policies", (AuthorizationDbContext db, IIdentityContext identity) =>
{
    var policies = db.Policies
        .Where(p => p.TenantKey == identity.TenantKey)
        .Select(p => new { p.Id, p.Name, p.ResourceName, p.Description, p.IsEnabled, p.Order })
        .ToList();

    return Results.Ok(policies);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
