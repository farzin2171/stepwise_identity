using Duende.IdentityServer;
using IdentityServerHost;
using IdentityServerHost.Services;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Needed starting this phase: IdentityServer redirects here (/Account/Login) whenever an authorize request
// can't be completed silently. Nothing before Phase 2 needed a UI, because nothing before Phase 2 could
// reach a state where IdentityServer had to ask a human anything.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<TenantContext>();
// Shared across every request for the process's lifetime — see ExternalUserStore.cs for why a scoped or
// transient lifetime wouldn't work here (first-login provisioning has to survive past the request that
// did it, for the token-issuance request that follows to see it).
builder.Services.AddSingleton<ExternalUserStore>();

builder.Services.AddIdentityServer(options =>
       {
           options.KeyManagement.Enabled = false;
       })
       .AddInMemoryIdentityResources(Config.IdentityResources)
       .AddInMemoryApiScopes(Config.ApiScopes)
       .AddInMemoryApiResources(Config.ApiResources)
       .AddInMemoryClients(Config.Clients)
       .AddDeveloperSigningCredential()
       // Registers TestUserStore in DI (AccountController takes a dependency on it) and a default
       // IResourceOwnerPasswordValidator/IProfileService pair backed by the same in-memory list. The real
       // IdG has no equivalent — it has no local password login at all, only external IdPs.
       .AddTestUsers(TestUsers.Users)
       // Overrides the default profile service AddTestUsers() just registered — that one only knows how
       // to answer "is this user active?" for subjects it finds in TestUsers.Users, which rejects the
       // externally-provisioned principal ExternalController.cs signs in. See SampleProfileService.cs.
       .AddProfileService<SampleProfileService>();

// IdentityServer's own cookies (idsrv, idsrv.external, idsrv.session) default to SameSite=None without
// Secure — logged as a framework warning. Relaxing to Lax is safe here because every hop in this sample
// stays on "localhost" as far as SameSite's site definition (scheme + registrable domain) is concerned.
builder.Services.ConfigureAll<CookieAuthenticationOptions>(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// A federated login is a login to ANOTHER app's IdentityServer, using this app as the relying party.
// SignInScheme = ExternalCookieAuthenticationScheme is the piece that makes this an "external" provider
// rather than replacing local login entirely: AddIdentityServer() above already registered that cookie
// scheme, and ExternalController below reads from it once, then discards it in favor of this app's own
// principal (IdG counterpart: Configurations/Authentication/*AuthenticationExtensions.cs).
builder.Services.AddAuthentication()
       .AddOpenIdConnect("external-idp", options =>
       {
           options.SignInScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;
           options.Authority = "http://localhost:5010";
           options.ClientId = "mini-idg-host";
           options.ClientSecret = "external-secret";
           options.ResponseType = "code";
           options.UsePkce = true;
           options.CallbackPath = "/signin-external-idp";
           options.RequireHttpsMetadata = false;   // local teaching sample only

           options.Scope.Clear();
           options.Scope.Add("openid");
           options.Scope.Add("profile");
           options.GetClaimsFromUserInfoEndpoint = true;   // "profile" alone isn't enough — see MvcClient's README
           options.MapInboundClaims = false;   // keep claim types exactly as ExternalIdp sends them

           options.CorrelationCookie.SameSite = SameSiteMode.Lax;
           options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
           options.NonceCookie.SameSite = SameSiteMode.Lax;
           options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
       });

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();

// After routing (so it only runs for requests that will actually be handled) and before IdentityServer
// itself, so both /connect/authorize and /Account/Login see a populated TenantContext by the time their
// handlers run.
app.UseMiddleware<TenantResolutionMiddleware>();

// UseIdentityServer() calls UseAuthentication() internally — order matters, and this is IdentityServer's
// own documented order: routing, then IdentityServer, then authorization, then endpoints.
app.UseIdentityServer();
app.UseAuthorization();

app.MapDefaultControllerRoute();

app.Run();
