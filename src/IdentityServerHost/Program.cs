using IdentityServerHost;
using IdentityServerHost.Configurations.Authentication;
using IdentityServerHost.Configurations.Authentication.Helpers;
using IdentityServerHost.Data;
using IdentityServerHost.ExternalServices;
using IdentityServerHost.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

// Same migrations assembly for all three contexts, and the one connection string that
// backs all of them — IdG counterpart: "persistence:serviceDb" section, bound to a
// SqlServerOptions type from an internal DIT package. This sample reads it directly, no
// custom options-binding layer needed for a single connection string.
var migrationsAssembly = typeof(Program).Assembly.GetName().Name;
var connectionString = builder.Configuration.GetConnectionString("IdentityServer");

// Needed starting this phase: IdentityServer redirects here (/Account/Login) whenever an authorize request
// can't be completed silently. Nothing before Phase 2 needed a UI, because nothing before Phase 2 could
// reach a state where IdentityServer had to ask a human anything.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<TenantContext>();
// Phase 5: backed by SQL Server now (UserDbContext), not a process-lifetime dictionary — so this can go
// back to the default scoped lifetime instead of AddSingleton<>(). See ExternalUserStore.cs.
builder.Services.AddDbContext<UserDbContext>(options =>
    options.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly)));
builder.Services.AddScoped<ExternalUserStore>();

// Bind target for the "ExternalProviders" config section — see appsettings.Development.json and
// docs/external-providers-configuration.md. IdG counterpart: the same section, loaded the same way, in
// externalproviderssettings.json.
builder.Services.Configure<ExternalProvidersOptions>(builder.Configuration.GetSection("ExternalProviders"));
builder.Services.AddSingleton<IAuthenticationHelper, AuthenticationHelper>();

// Phase 7: bind target for "ExternalServicesApi" — see appsettings.Development.json and
// ExternalServices/ExternalServicesOptions.cs.
builder.Services.Configure<ExternalServicesOptions>(builder.Configuration.GetSection("ExternalServicesApi"));

// Backs the tenant-GUID cache in SampleProfileService — an in-memory IDistributedCache instead of the
// real system's Redis, same interface either way. See SampleProfileService.cs for why this is the
// wrong place for that cache to live forever, on purpose.
builder.Services.AddDistributedMemoryCache();

// Same Polly retry + circuit-breaker shape MvcClient already established for its own external calls
// (Program.cs there) — reused verbatim rather than reinvented.
static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
    HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(2, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

static IAsyncPolicy<HttpResponseMessage> CircuitBreakerPolicy() =>
    HttpPolicyExtensions.HandleTransientHttpError()
        .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));

builder.Services.AddHttpClient<TenantClient>()
       .AddPolicyHandler(RetryPolicy())
       .AddPolicyHandler(CircuitBreakerPolicy());
builder.Services.AddHttpClient<UserClient>()
       .AddPolicyHandler(RetryPolicy())
       .AddPolicyHandler(CircuitBreakerPolicy());

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
       // Phase 5: Duende's own stock EF stores, SQL Server-backed — the same types the real IdG uses,
       // no custom IClientStore/IResourceStore (it doesn't have those either; only a custom
       // IdentityProviderStore, not yet ported here). Phase 6: rows come from
       // ../../Tools/ConfigIngestionTool now, not a seed step in this app — Duende just reads whatever
       // is in the database.
       .AddConfigurationStore(options =>
       {
           options.ConfigureDbContext = b =>
               b.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly));
       })
       // Persisted grants (refresh tokens, authorization codes, device codes, consent) — previously
       // in-memory and gone on every restart along with everything else Phase 5 fixes.
       .AddOperationalStore(options =>
       {
           options.ConfigureDbContext = b =>
               b.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly));
       })
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

// Applies pending migrations for all three DbContexts — runs on every startup, idempotent. As of
// Phase 6, this no longer seeds any rows: run ../../Tools/ConfigIngestionTool separately to populate
// Clients/Resources from Configurations/IdentityServerConfig.json. See Data/SeedData.cs.
SeedData.EnsureDatabasesMigrated(app.Services);

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
