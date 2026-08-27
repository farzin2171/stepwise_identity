using IdentityServerHost;
using IdentityServerHost.Configurations.Authentication;
using IdentityServerHost.Configurations.Authentication.Helpers;
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

// Bind target for the "ExternalProviders" config section — see appsettings.Development.json and
// docs/external-providers-configuration.md. IdG counterpart: the same section, loaded the same way, in
// externalproviderssettings.json.
builder.Services.Configure<ExternalProvidersOptions>(builder.Configuration.GetSection("ExternalProviders"));
builder.Services.AddSingleton<IAuthenticationHelper, AuthenticationHelper>();

builder.Services.AddIdentityServer(options =>
       {
           options.KeyManagement.Enabled = false;

           // NOT covered by the ConfigureAll<CookieAuthenticationOptions> fix below — this cookie is
           // written directly by Duende's own session service (DefaultUserSession), not through the
           // standard ASP.NET Core cookie-auth handler, so CookieAuthenticationOptions never touches it.
           // Defaults to None specifically to support cross-origin check-session-iframe monitoring, a
           // feature neither MvcClient nor ReactSpa implements — relaxing it here is safe and closes the
           // one cookie the blanket fix below was previously (incorrectly) documented as covering.
           options.Authentication.CheckSessionCookieSameSiteMode = SameSiteMode.Lax;
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

// IdentityServer's own cookies (idsrv, idsrv.external) default to SameSite=None without Secure —
// logged as a framework warning, and silently DROPPED outright by a real browser (not just warned
// about) since a real browser refuses to store a SameSite=None cookie that isn't also Secure, and this
// sample runs on plain HTTP. Relaxing to Lax is safe here because every hop in this sample stays on
// "localhost" as far as SameSite's site definition (scheme + registrable domain) is concerned.
// idsrv.session is NOT covered by this — see CheckSessionCookieSameSiteMode above for why it needs its
// own, separate fix.
builder.Services.ConfigureAll<CookieAuthenticationOptions>(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// A federated login is a login to ANOTHER app's IdentityServer, using this app as the relying party.
// Every provider under the "ExternalProviders" config section gets registered here, one
// AddOpenIdConnect() call each — see Configurations/Authentication/ExternalProviderAuthenticationExtensions.cs.
// IdG counterpart: AddExternalProvidersFromFile(_configuration) in Startup.cs.
builder.Services.AddAuthentication()
       .AddExternalProvidersFromFile(builder.Configuration);

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
