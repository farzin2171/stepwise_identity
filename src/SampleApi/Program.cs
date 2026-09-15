using System.Text.Json.Serialization;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Mini.Infrastructure.ExternalServices;
using Mini.Infrastructure.Http;
using Mini.Infrastructure.Identity;

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

           // Must match the ApiResource name in IdentityServerHost/Configurations/IdentityServerConfig.json — Duende stamps that name
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

// Port of Services.Authorization's IIdentityContext — see Mini.Infrastructure/Identity for the
// comparison to the real DIT.Identity library this was ported from.
builder.Services.AddScoped<IIdentityContext, IdentityContext>();

// Phase 14. HTTP client to Mini.AuthorizationService (:5015). The service evaluates policies per tenant,
// answering "is this caller authorized for this resource?" SampleApi calls it for authorization decisions
// that can change without re-issuing tokens.
//
// Phase 16: extracted into Mini.Infrastructure/ExternalServices/AuthorizationClient.cs (see that file's
// header for the DIT.Authorization.Client comparison) and switched from a one-off HttpClientHandler with
// a hand-rolled certificate bypass to the same typed-client + Polly convention every other named client in
// this repo uses (compare IdentityServerHost/Program.cs's AddHttpClient<TenantClient>()). The certificate
// bypass was never actually needed — every other https://localhost client in this repo relies on `dotnet
// dev-certs trust` instead, and Mini.AuthorizationService's dev cert is no different; it was dead caution
// nobody had gotten around to removing. Retry/circuit-breaker were previously entirely absent for this
// call, unlike every other cross-service HTTP call in the repo — a gap this extraction closes rather than
// carries forward.
builder.Services.Configure<ExternalServicesConfiguration>(builder.Configuration.GetSection("ExternalServicesApi"));

builder.Services.AddHttpClient<IAuthorizationClient, AuthorizationClient>((services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("AuthorizationService");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
       })
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

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

// Phase 14. Authorization evaluation endpoint — calls Mini.AuthorizationService to determine if the
// caller is authorized for a resource. This is the integration point: instead of embedding authorization
// decisions in the token at issuance time (the `role` claim), the API asks the authorization service
// at request time. Authorization decisions can now change without re-issuing tokens.
//
// The real Services.Authorization has Authorize and Evaluate endpoints that take policy names; this
// is simplified to just ask about a named resource.
api.MapPost("/authorize/{resourceName}", async (
    string resourceName,
    IIdentityContext identityContext,
    IAuthorizationClient authzClient,
    HttpContext ctx) =>
{
    // Extract the role from the token if present, for context. Phase 13 left the role claim in the token
    // on purpose, so the authorization service can compare its own decision against what the token said.
    var roleFromToken = ctx.User.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "Member";

    var context = new Dictionary<string, string>
    {
        { "role", roleFromToken },
        { "caller", identityContext.IdentityType.ToString() }
    };

    // Forward this request's own bearer token as-is — Mini.AuthorizationService needs the real
    // caller's identity (subject, tenant) to evaluate per-tenant policies, which a generic
    // service-to-service credential wouldn't carry. That's why Mini.AuthorizationService accepts
    // this token's "api1" audience alongside "authapi" (see its Program.cs).
    var inboundToken = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
    var result = await authzClient.EvaluateAsync(resourceName, identityContext, inboundToken, context);

    return Results.Ok(new
    {
        authorized = result.Authorized,
        reason = result.Reason,
        callerId = identityContext.ClientId ?? identityContext.Subject,
        resourceName,
        roleFromToken
    });
}).RequireAuthorization("ApiScope");

// Port of Services.Authorization's CacheController.Delete — service-to-service cache invalidation,
// gated to service accounts only. Deliberately has no .RequireAuthorization() call:
// ServiceAccountOnlyFilter alone decides both "is there a caller at all" (401) and "is that caller a
// service account" (403), exactly like the real ServiceAccountAuthorizeFilter it was ported from —
// see Mini.Infrastructure/Identity/ServiceAccountOnlyFilter.cs.
//
// Phase 15: forwards to Mini.AuthorizationService's own DELETE /cache/{tenantKey}, which now backs a
// real, persisted decision cache — this is no longer a simulation. The caller's own bearer token
// (already proven to be a service account by the filter above) is forwarded as-is: the authorization
// service re-checks IdentityType == Service itself rather than trusting SampleApi's say-so.
api.MapDelete("/admin/cache/{tenantKey}", async (string tenantKey, HttpContext ctx, IAuthorizationClient authzClient) =>
{
    var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
    var message = await authzClient.ClearCacheAsync(tenantKey, token);
    return Results.Ok(new { message });
}).AddEndpointFilter<ServiceAccountOnlyFilter>();

// Phase 10: an unauthenticated liveness probe, so run-all.ps1 can tell "this process is listening and
// finished starting" from "this port is open but the app is still warming up." Deliberately the plainest
// thing that answers that question — the real services use DIT.HealthChecks, which additionally reports
// on each dependency (database, downstream service) and is what an orchestrator scrapes.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
