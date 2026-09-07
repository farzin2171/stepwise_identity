using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mini.Infrastructure.ExternalServices;
using Mini.Infrastructure.Http;
using Mini.Infrastructure.Identity;
using Mini.UserService.Data;
using Mini.UserService.Endpoints;
using Mini.UserService.ExternalServices;

// Replaces ExternalServicesStub (Phase 7) with a real service: its own SQL Server database, its own
// migrations, EF-backed lookups instead of Dictionary literals, an authenticated management API that
// can add a tenant without a code edit, and an outbound call of its own back into IdentityServerHost.
//
// The stub is NOT deleted — see ../ExternalServicesStub/README.md. A phase course's value is the diff
// between phases, and the stub is the "before" half of this one.
var builder = WebApplication.CreateBuilder(args);

// Same reason SampleApi does this: IdentityType would otherwise serialize as 0/1 instead of
// "User"/"Service", and this service's /identity diagnostic exists to be read by a human.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Named ServiceDbContext after the real thing, pointed at a database called MiniUsers — NOT the
// MiniIdG that IdentityServerHost's three contexts share. See Data/ServiceDbContext.cs for why that
// separation is the point of this phase and not a detail.
builder.Services.AddDbContext<ServiceDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("ServiceDb")));

// Both surfaces validate tokens the same way SampleApi does — against IdentityServerHost's published
// JWKS, fetched once from its discovery document. No shared secret, no per-request round trip. This is
// unchanged from the stub; it was the one thing the stub already did properly.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           options.Authority = builder.Configuration["Authentication:Authority"];
           options.RequireHttpsMetadata = false;

           // Both audiences are accepted at the handler, then narrowed per endpoint by the two policies
           // below. The handler has to accept both because one process serves both collapsed services;
           // the policies are what stop that collapse from becoming a privilege merge.
           options.TokenValidationParameters.ValidAudiences = ["tenantmgntapi", "userapi"];

           // Without this, "sub" arrives as the long ClaimTypes.NameIdentifier URI and
           // IdentityContext's FindFirst("sub") returns null — which would silently classify every
           // user token as a service account. SampleApi sets the same flag for the same reason.
           options.MapInboundClaims = false;
       });

builder.Services.AddAuthorization(options =>
{
    // In production these two audiences belong to two different services, deployed separately, each
    // unable to serve the other's routes. Collapsed into one process, an authorization policy is the
    // only thing left enforcing that boundary — so it's enforced explicitly rather than left to the
    // fact that nobody happens to call the wrong route.
    options.AddPolicy("TenantApi", policy => policy.RequireClaim("aud", "tenantmgntapi"));
    options.AddPolicy("UserApi", policy => policy.RequireClaim("aud", "userapi"));

    // These two additionally require the SCOPE, and that extra clause is not belt-and-braces — it is
    // the fix for a real hole this phase opened and then found by looking.
    //
    // ServiceAccountOnlyFilter answers "is this caller a service account" by checking for the absence
    // of a "sub" claim (Mini.Infrastructure/Identity/IdentityContext.cs). IdentityServerHost's
    // self-issued read JWT has no "sub" either, so it reads as a service account too — verified, not
    // assumed: pointed UserClient at this service's own /api/v2/identity diagnostic and got back
    // {"identityType":"Service","subject":null,"clientId":"identityserverhost","tenantKey":null}.
    // Same "userapi" audience as the management surface. So with the filter alone, the token the IdG
    // sends to READ a role would have been accepted to WRITE one.
    //
    // What the self-issued JWT does NOT have is a "scope" claim — IssueClientJwtAsync stamps iss, nbf,
    // iat, exp, client_id and aud, and nothing else. A scope can only be obtained by going through
    // /connect/token as a registered client, which is exactly the property that makes a credential
    // revocable and auditable. So requiring it here draws the line the filter can't.
    //
    // The real system draws the same line with an explicit "service_isService" claim
    // (Libraries.Infrastructure/DIT.Identity) rather than a scope, and that is the more general fix:
    // it distinguishes caller KINDS instead of caller PERMISSIONS. This sample has two identity types
    // (see Identity/IdentityType.cs) for what turns out to be three kinds of caller — a user, a
    // registered service account, and a host issuing itself a token — and closing the gap with a scope
    // is the narrow fix available without inventing the third type.
    options.AddPolicy("TenantApiManagement", policy => policy
        .RequireClaim("aud", "tenantmgntapi")
        .RequireClaim("scope", "tenantmgntapi"));
    options.AddPolicy("UserApiManagement", policy => policy
        .RequireClaim("aud", "userapi")
        .RequireClaim("scope", "userapi"));
});

// The third consumer of Mini.Infrastructure's Identity folder, after SampleApi and (indirectly)
// MvcClient — Phase 10's table predicted "two more arrive in Phases 11 and 14," and this is the first.
builder.Services.AddScoped<IIdentityContext, IdentityContext>();

// Backs TokenClient's cache of service-account tokens, exactly as in MvcClient.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ITokenClient, TokenClient>();
builder.Services.Configure<ExternalServicesConfiguration>(builder.Configuration.GetSection("ExternalServicesApi"));

// Named client for the outbound call to IdentityServerHost, base address from the service registry
// rather than hardcoded — the same ServiceDefinitions pattern MvcClient uses for SampleApi.
builder.Services.AddHttpClient("IdentityGateway", (services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           client.BaseAddress = new Uri(externalServices.GetServiceDefinition("IdentityGateway").GetFullPath());
       })
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

// TokenClient resolves this by name — CreateClient("token"). Same resilience treatment: a flaky token
// endpoint is as much a transient-failure situation as a flaky downstream API.
builder.Services.AddHttpClient("token")
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

builder.Services.AddScoped<IdentityGatewayClient>();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Applies pending migrations on startup, including the HasData rows for the three tenants and alice's
// role. Idempotent, and it creates the MiniUsers database on first run — same shape as
// IdentityServerHost's SeedData.EnsureDatabasesMigrated, so run-all.ps1 needs no extra step for this
// service. Unlike Phase 6's ingestion split, there is no separate config document to load: the
// baseline reference data travels in the migration itself.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<ServiceDbContext>().Database.Migrate();
}

app.UseExceptionHandler();
app.UseAuthentication();

// After UseAuthentication (needs ctx.User) and before UseAuthorization (so the policies and
// ServiceAccountOnlyFilter below can rely on IIdentityContext).
app.UseMiddleware<IdentityContextMiddleware>();

app.UseAuthorization();

app.MapTenantEndpoints();
app.MapUserEndpoints();
app.MapManagementEndpoints();

// The same diagnostic SampleApi exposes, for the same reason: when a service-to-service call is
// refused, the first question is always "who did the callee think was calling," and guessing at it
// from a 403 is slow. Requires the User audience, so it's reachable by anything holding a token this
// service would accept on its user surface.
app.MapGet("/api/v2/identity", (HttpContext ctx, IIdentityContext identityContext) => Results.Ok(new
{
    identity = new
    {
        identityContext.IdentityType,
        identityContext.Subject,
        identityContext.ClientId,
        identityContext.TenantKey
    },
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("UserApi");

// Unauthenticated liveness probe, so run-all.ps1 can tell "listening and finished starting" from "port
// is open but still warming up." The real service's /health is basic-auth gated and additionally checks
// SQL, the Logging service and the Authorization service (DIT.HealthChecks); this one answers only the
// question run-all.ps1 asks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
