using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SampleApi.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Without this, IdentityType serializes as its underlying int (0/1) instead of "User"/"Service" —
// readable JSON matters for a field whose whole point is to be inspected in a response body.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           // Where to find the discovery document + JWKS. The middleware fetches
           // /.well-known/openid-configuration from here once (then caches it), reads jwks_uri from it,
           // and validates every incoming token's signature against those keys — no shared secret, no
           // per-request round trip back to IdentityServerHost.
           options.Authority = "https://localhost:5001";

           // Local teaching sample only: the Authority above is plain HTTP. A real API requires HTTPS
           // everywhere — never disable this in real code.
           options.RequireHttpsMetadata = false;

           // Must match the ApiResource name in IdentityServerHost/Config.cs — Duende stamps that name
           // into the token's "aud" claim. A token issued for a different audience is rejected here
           // before this API's own code ever runs.
           options.TokenValidationParameters.ValidAudience = "api1";

           options.MapInboundClaims = false;
       });

builder.Services.AddAuthorization(options =>
{
    // [Authorize] alone only checks "is this token valid" — it says nothing about what the token was
    // issued for. This policy additionally requires the "api1" scope claim, so a valid token minted for
    // some other API (with no "api1" scope) still gets refused here.
    options.AddPolicy("ApiScope", policy => policy.RequireClaim("scope", "api1"));
});

// Port of Services.Authorization's IIdentityContext — see Infrastructure/Identity for the
// comparison to the real DIT.Identity library this was ported from.
builder.Services.AddScoped<IIdentityContext, IdentityContext>();

// Services.Authorization versions every route (api/v{version:apiVersion}/...) via Asp.Versioning
// (the MVC package, since it's a Controllers app). This is Asp.Versioning.Http — the minimal-API
// counterpart — but the route convention and intent are identical.
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1.0);
    options.ReportApiVersions = true;
});

// Simplified stand-in for Services.Authorization's custom RFC7807 ProblemDetailsMiddleware
// (Libraries.Infrastructure/DIT.WebApi). Paired with UseExceptionHandler() below, this turns an
// unhandled exception into a 500 problem+json response instead of a blank one — it does NOT,
// contrary to how it reads, automatically add a body to every 4xx/5xx in the app (the JWT Bearer
// 401 challenge below never goes through it — see docs/identity-context-and-conventions.md §4 for
// what actually does and doesn't get a body, and why).
builder.Services.AddProblemDetails();

// MvcClient never needed this — it calls this API from a server-to-server HttpClient, not from a
// browser. ReactSpa calls it with the browser's own fetch(), from a different origin (:5173 vs :5003),
// which makes this a CORS request: without an explicit allow-list, the browser blocks the preflight
// before the real GET (with its Authorization header) is ever sent.
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactSpa", policy => policy
        .WithOrigins("http://localhost:5173")
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

// Turns an unhandled exception into a 500 problem+json response (via AddProblemDetails() above)
// instead of a blank response / the developer exception page. Nothing in this sample throws on
// purpose, so this isn't exercised by the test script — it's here because a real API needs it, not
// because this one demonstrates it.
app.UseExceptionHandler();

app.UseCors("ReactSpa");
app.UseAuthentication();

// Must run after UseAuthentication() (needs ctx.User already populated) and before
// UseAuthorization() (so policies/filters further down can rely on IIdentityContext).
app.UseMiddleware<IdentityContextMiddleware>();

app.UseAuthorization();

var versionSet = app.NewApiVersionSet()
    .HasApiVersion(new ApiVersion(1.0))
    .ReportApiVersions()
    .Build();

var api = app.MapGroup("/api/v{version:apiVersion}")
    .WithApiVersionSet(versionSet)
    .HasApiVersion(1.0);

api.MapGet("/identity", (HttpContext ctx, IIdentityContext identityContext) => Results.Ok(new
{
    message = "Hello from SampleApi — you only see this because your access token passed signature, " +
              "expiry, issuer, audience, and scope validation.",
    identity = new
    {
        identityContext.IdentityType,
        identityContext.Subject,
        identityContext.ClientId,
        identityContext.TenantKey
    },
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("ApiScope");

// Port of Services.Authorization's CacheController.Delete — service-to-service cache invalidation,
// gated to service accounts only. No real cache exists in this sample, so it just echoes what it
// would have cleared. Deliberately has no .RequireAuthorization() call: ServiceAccountOnlyFilter
// alone decides both "is there a caller at all" (401) and "is that caller a service account" (403),
// exactly like the real ServiceAccountAuthorizeFilter it was ported from — see
// Infrastructure/Identity/ServiceAccountOnlyFilter.cs.
api.MapDelete("/admin/cache/{tenantKey}", (string tenantKey) => Results.Ok(new
{
    message = $"Cache cleared for tenant '{tenantKey}' (simulated — this sample has no real cache)."
})).AddEndpointFilter<ServiceAccountOnlyFilter>();

app.Run();
