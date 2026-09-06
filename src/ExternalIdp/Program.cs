using ExternalIdp;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddIdentityServer(options =>
       {
           options.KeyManagement.Enabled = false;

           // Not covered by ConfigureAll<CookieAuthenticationOptions> below — written directly by
           // Duende's own session service, not the standard cookie-auth handler. See
           // IdentityServerHost/Program.cs for the full explanation.
           options.Authentication.CheckSessionCookieSameSiteMode = SameSiteMode.Lax;
       })
       .AddInMemoryIdentityResources(Config.IdentityResources)
       .AddInMemoryApiScopes(Config.ApiScopes)
       .AddInMemoryClients(Config.Clients)
       .AddDeveloperSigningCredential()
       .AddTestUsers(TestUsers.Users);

// Same fix IdentityServerHost needed in Phase 2, ported here (this project is ALSO a Duende
// IdentityServer, so it has the exact same cookies with the exact same SameSite=None-without-Secure
// default). Missing this on THIS project specifically is what made federated login fail in a real
// browser while every HttpClient-based test script kept passing: HttpClient stores and resends cookies
// unconditionally, ignoring both Secure and SameSite — a real browser enforces both and silently drops
// a SameSite=None cookie sent without Secure over plain HTTP. Without this fix, this app's own "idsrv"
// authentication cookie never survives the redirect back into its own /connect/authorize/callback, so
// it never sees Carol as signed in and re-shows its login page instead of completing the flow.
builder.Services.ConfigureAll<CookieAuthenticationOptions>(options =>
{
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseIdentityServer();
app.UseAuthorization();

app.MapDefaultControllerRoute();

// Phase 10: an unauthenticated liveness probe, so run-all.ps1 can tell "this process is listening and
// finished starting" from "this port is open but the app is still warming up." Deliberately the plainest
// thing that answers that question — the real services use DIT.HealthChecks, which additionally reports
// on each dependency (database, downstream service) and is what an orchestrator scrapes.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
