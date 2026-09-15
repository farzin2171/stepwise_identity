using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Phase 17: a SECOND client app logging into the same IdentityServerHost as MvcClient, with its own
// client registration ("agentportal" — see IdentityServerConfig.json) instead of reusing "mvcclient"'s.
// That's the entire point of this project: prove a second, independently-configured client can coexist
// on the same IdG, the way `Applications.Apply` is one of several Equisoft client apps hitting the same
// production Identity Gateway. See README.md's "Phase 17" section for what's deliberately NOT here yet
// (tenant resolution, a downstream API call, Mini.AuthorizationService) — those are MvcClient features
// this skeleton hasn't grown, not omissions.
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

           options.SaveTokens = true;
           options.GetClaimsFromUserInfoEndpoint = true;
           options.MapInboundClaims = false;

           // Same reason MvcClient needs this: SameSite=None (the OIDC handler's default for the
           // correlation/nonce cookies) requires Secure, which this app's plain-HTTP-between-processes
           // localhost setup can't satisfy — see MvcClient/Program.cs's identical comment.
           options.CorrelationCookie.SameSite = SameSiteMode.Lax;
           options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
           options.NonceCookie.SameSite = SameSiteMode.Lax;
           options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
       });

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapDefaultControllerRoute();

// Same plain, unauthenticated liveness probe every other host in this sample exposes — see
// MvcClient/Program.cs's comment on why this is deliberately the simplest thing that answers "is this
// process up," not DIT.HealthChecks.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
