using IdentityServerHost;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

// Needed starting this phase: IdentityServer redirects here (/Account/Login) whenever an authorize request
// can't be completed silently. Nothing before Phase 2 needed a UI, because nothing before Phase 2 could
// reach a state where IdentityServer had to ask a human anything.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<TenantContext>();

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
       // IdG has no equivalent — it has no local password login at all, only external IdPs (Phase 4).
       .AddTestUsers(TestUsers.Users);

// IdentityServer's own cookies (idsrv, idsrv.external, idsrv.session) default to SameSite=None without
// Secure — logged as a framework warning. Relaxing to Lax is safe here because every hop in this sample
// stays on "localhost" as far as SameSite's site definition (scheme + registrable domain) is concerned.
builder.Services.ConfigureAll<CookieAuthenticationOptions>(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
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
