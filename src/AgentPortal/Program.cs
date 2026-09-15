using AgentPortal.Data;
using AgentPortal.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mini.Infrastructure.ExternalServices;
using Mini.Infrastructure.Http;
using Mini.Infrastructure.Identity;
using Mini.Infrastructure.MultiTenant;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Phase 22: Agent Portal's first database — an audit trail, not a copy of Mini.AuthorizationService's
// own Policy rows (see Data/AgentPortalDbContext.cs's header comment). Same
// AddDbContext<TContext>(UseSqlServer(...)) shape every other LocalDB-backed project in this repo uses
// (Mini.AuthorizationService's Program.cs is the closest example).
builder.Services.AddDbContext<AgentPortalDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("AgentPortalDb"),
        b => b.MigrationsAssembly("AgentPortal")));

// Phase 18: gives this skeleton a reason to exist — tenant resolution plus a real downstream call.
// ITenantContext/TenantContext/TenantResolutionMiddleware are Mini.Infrastructure's Phase-18 extraction
// of what used to be MvcClient's own private copy (see Mini.Infrastructure/MultiTenant's README section) —
// AgentPortal is the second, genuine consumer that triggered the move, wired here exactly the way
// MvcClient/Program.cs wires the same three types.
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Port of Services.Authorization's IIdentityContext — same type SampleApi and Mini.AuthorizationService
// already use for a caller with no browser at all. AgentPortal is the first BROWSER-based consumer: no
// new type was invented for it (see README.md's Phase 18 section for why the existing shape already
// fits — a signed-in user's own claims populate it exactly like a forwarded user token would).
builder.Services.AddScoped<IIdentityContext, IdentityContext>();

// Same config-driven service registry SampleApi and MvcClient already use — see
// Mini.Infrastructure/ExternalServices/ExternalServicesConfiguration.cs.
builder.Services.Configure<ExternalServicesConfiguration>(builder.Configuration.GetSection("ExternalServicesApi"));

// Phase 18. The shared, resilient authorization client extracted into Mini.Infrastructure in Phase 16 —
// AgentPortal is its second real consumer (SampleApi is the first). Same
// AddHttpClient<T>().AddPolicyHandler(...) shape SampleApi's Program.cs uses, pointed at
// Mini.AuthorizationService (:5015) instead of being duplicated.
builder.Services.AddHttpClient<IAuthorizationClient, AuthorizationClient>((services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("AuthorizationService");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
       })
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

// Phase 22: a second, small typed client at the SAME base address (Mini.AuthorizationService, :5015)
// as IAuthorizationClient above — see Services/PolicyAdminClient.cs's header comment for why this one
// stays local to AgentPortal instead of moving into Mini.Infrastructure alongside IAuthorizationClient.
builder.Services.AddHttpClient<IPolicyAdminClient, PolicyAdminClient>((services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("AuthorizationService");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
           // Phase 22 "Things that broke": Mini.AuthorizationService's PUT endpoint (Phase 21) awaits
           // IPublishEndpoint.Publish before responding, and MassTransit's RabbitMQ transport has no
           // publish timeout of its own — with the broker unreachable, that await never completes, so
           // the default HttpClient.Timeout (100s) was the only thing that ever ended the request, and
           // it did so as an unhandled-looking TaskCanceledException. An explicit, shorter timeout here
           // turns a broker outage into a fast, RECORDED "Failed" PolicyChangeRequest row (see
           // PolicyAdminClient's catch block) instead of a hung request — the whole point of having an
           // Outcome field at all. See README.md's Phase 22 section for how this was found.
           client.Timeout = TimeSpan.FromSeconds(15);
       })
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

// Phase 17: a SECOND client app logging into the same IdentityServerHost as MvcClient, with its own
// client registration ("agentportal" — see IdentityServerConfig.json) instead of reusing "mvcclient"'s.
// That's the entire point of this project: prove a second, independently-configured client can coexist
// on the same IdG, the way `Applications.Apply` is one of several Equisoft client apps hitting the same
// production Identity Gateway.
builder.Services.AddAuthentication(options =>
       {
           options.DefaultScheme = "cookies";
           options.DefaultChallengeScheme = "oidc";
       })
       .AddCookie("cookies")
       .AddOpenIdConnect("oidc", options =>
       {
           options.Authority = "https://localhost:5001";
           options.ClientId = "agentportal";
           options.ClientSecret = "agentportal-secret";

           options.ResponseType = "code";
           options.UsePkce = true;

           // Local teaching sample only: see MvcClient/Program.cs for why this is safe here and never in
           // real code.
           options.RequireHttpsMetadata = false;

           // Same PAR gotcha MvcClient hit in Phase 3 (see its README) doesn't bite this project — Agent
           // Portal never sets acr_values, so it doesn't matter whether the authorize redirect is visible
           // on the query string or hidden behind a pushed_authorization_request_endpoint. Left at the
           // handler's default (UseIfAvailable) rather than copying MvcClient's Disable for that reason —
           // see "Where this sample simplifies" in README.md.
           options.Scope.Clear();
           options.Scope.Add("openid");
           options.Scope.Add("profile");
           // Phase 18. Asking for "api1" is what puts an access token good for calling
           // Mini.AuthorizationService (via SampleApi's own "api1"/"authapi" dual-audience acceptance —
           // see that service's Program.cs) into the token response. Asking for "tenant" is what puts
           // "tenant_id"/"role" onto that same token — see IdentityServerConfig.json's identityResources
           // entry for "tenant".
           options.Scope.Add("api1");
           options.Scope.Add("tenant");

           options.SaveTokens = true;
           options.GetClaimsFromUserInfoEndpoint = true;
           options.MapInboundClaims = false;

           // Same gotcha MvcClient's README documents under its own Phase 3 section: the OIDC handler
           // only merges userinfo claims it has a registered ClaimAction for, and "tenant_id"/"role" are
           // not standard OIDC claims — without these two lines they're silently dropped, no error, and
           // TenantResolutionMiddleware would never see a tenant_id to resolve.
           options.ClaimActions.MapUniqueJsonKey("tenant_id", "tenant_id");
           options.ClaimActions.MapUniqueJsonKey("role", "role");

           // Same reason MvcClient needs this: SameSite=None (the OIDC handler's default for the
           // correlation/nonce cookies) requires Secure, which this app's plain-HTTP-between-processes
           // localhost setup can't satisfy — see MvcClient/Program.cs's identical comment.
           options.CorrelationCookie.SameSite = SameSiteMode.Lax;
           options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
           options.NonceCookie.SameSite = SameSiteMode.Lax;
           options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
       });

var app = builder.Build();

// Same placement Mini.AuthorizationService's Program.cs uses for its own DbContext: migrate
// immediately after Build(), before the request pipeline is wired up, so a missing/behind database
// fails fast at startup rather than on this app's first policy-edit request.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AgentPortalDbContext>();
    db.Database.Migrate();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

// Same ordering constraint MvcClient/Program.cs documents: TenantResolutionMiddleware needs
// ctx.User already populated (after UseAuthentication) and must run before UseAuthorization()/endpoint
// execution so RequireTenant and controller code see a populated ITenantContext.
app.UseMiddleware<TenantResolutionMiddleware>();

// Same ordering constraint SampleApi/Program.cs documents for the exact same middleware.
app.UseMiddleware<IdentityContextMiddleware>();

app.UseAuthorization();

app.MapDefaultControllerRoute();

// Same plain, unauthenticated liveness probe every other host in this sample exposes — see
// MvcClient/Program.cs's comment on why this is deliberately the simplest thing that answers "is this
// process up," not DIT.HealthChecks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
