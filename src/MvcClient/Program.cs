var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Named client for calling SampleApi. HomeController attaches the current user's access token to every
// request made through this client — see Controllers/HomeController.cs.
builder.Services.AddHttpClient("SampleApi", client =>
{
    client.BaseAddress = new Uri("http://localhost:5003");
});

builder.Services.AddAuthentication(options =>
       {
           // "cookies" holds this app's own session. "oidc" is only used to *establish* that session — it
           // never runs again until the cookie expires and a fresh challenge is needed.
           options.DefaultScheme = "cookies";
           options.DefaultChallengeScheme = "oidc";
       })
       .AddCookie("cookies")
       .AddOpenIdConnect("oidc", options =>
       {
           options.Authority = "http://localhost:5000";
           options.ClientId = "mvcclient";
           options.ClientSecret = "secret";

           // Authorization Code + PKCE. This app can keep ClientSecret out of the browser (it runs
           // server-side), so it doesn't strictly need PKCE the way a SPA does — but the IdG requires it
           // on every client, confidential or not, so this sample matches that.
           options.ResponseType = "code";
           options.UsePkce = true;

           // Local teaching sample only: the Authority above is plain HTTP. The IdG requires HTTPS
           // everywhere — never disable this in real code.
           options.RequireHttpsMetadata = false;

           options.Scope.Clear();
           options.Scope.Add("openid");
           options.Scope.Add("profile");
           // Asking for this scope is what puts an access token this app is allowed to hand to SampleApi
           // into the token response — without it, SaveTokens still stores an access token, but SampleApi
           // rejects it (no "api1" in its scope claims, so the ApiScope policy fails).
           options.Scope.Add("api1");

           // Keeps the id_token/access_token in the auth cookie so the Secure view below can print them.
           options.SaveTokens = true;

           // "profile" being in Scope doesn't put profile claims in the ID token by itself — Duende only
           // puts sub (and a few protocol-required claims) there for the code flow, on the assumption a
           // confidential client will ask for the rest itself. This is that ask: an automatic call to
           // /connect/userinfo after the token exchange, merging its claims into the principal.
           options.GetClaimsFromUserInfoEndpoint = true;

           options.MapInboundClaims = false;

           // The correlation and nonce cookies default to SameSite=None (required because the IdP's
           // form_post callback is a cross-origin POST back to this app) which in turn requires Secure —
           // i.e. HTTPS only. This sample runs both apps over plain HTTP, so Secure cookies would never be
           // sent back and every login would fail with "Correlation failed." Lax is the right relaxation
           // here specifically because localhost:5000 and localhost:5002 are *same-site* (SameSite is
           // defined by scheme + registrable domain, not port) — a real cross-site deployment would need
           // real HTTPS instead of this downgrade.
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

app.Run();
