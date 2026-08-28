using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using MvcClient.Infrastructure.Configuration;
using MvcClient.Infrastructure.Externals;
using MvcClient.Infrastructure.MultiTenant;
using Polly;
using Polly.Extensions.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

// Bind targets for the two config sections ported from Applications.Apply — see
// docs/multitenancy-and-external-services.md for the full field-by-field reference.
builder.Services.Configure<IdentityGatewayConfiguration>(builder.Configuration.GetSection("IdentityGatewayApi"));
builder.Services.Configure<ExternalServicesConfiguration>(builder.Configuration.GetSection("ExternalServicesApi"));

// Scoped: one instance per request, written once by TenantResolutionMiddleware, read by everything
// downstream. See Infrastructure/MultiTenant/ITenantContext.cs.
builder.Services.AddScoped<ITenantContext, TenantContext>();

// Backs TokenClient's cache of service-account tokens — see Infrastructure/Externals/TokenClient.cs.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ITokenClient, TokenClient>();

// Same Polly retry + circuit-breaker shape Apply wraps around every one of its external HTTP clients
// (Infrastructure/Http/ServiceCollectionExtensions.cs there) — applied here to both named clients this
// app uses. A transient failure gets retried with exponential backoff; enough consecutive failures trip
// the breaker and fail fast instead of piling up slow, doomed requests against a service that's down.
static IAsyncPolicy<HttpResponseMessage> RetryPolicy() =>
    HttpPolicyExtensions.HandleTransientHttpError()
        .WaitAndRetryAsync(2, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

static IAsyncPolicy<HttpResponseMessage> CircuitBreakerPolicy() =>
    HttpPolicyExtensions.HandleTransientHttpError()
        .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));

// Named client for calling SampleApi. Base address now comes from ExternalServicesApi's
// ServiceDefinitions["SampleApi"] instead of being hardcoded here — see
// Infrastructure/Configuration/ExternalServicesConfiguration.cs. HomeController attaches a Bearer token
// to every request made through this client (either the signed-in user's own token, or a service-account
// token from ITokenClient — see Controllers/HomeController.cs for both).
builder.Services.AddHttpClient("SampleApi", (services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("SampleApi");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
       })
       .AddPolicyHandler(RetryPolicy())
       .AddPolicyHandler(CircuitBreakerPolicy());

// Named client for TokenClient's client-credentials requests to IdentityServerHost's /connect/token.
// Same resilience treatment as SampleApi — a flaky token endpoint is just as much a "this call might
// transiently fail" situation as a flaky downstream API.
builder.Services.AddHttpClient("token")
       .AddPolicyHandler(RetryPolicy())
       .AddPolicyHandler(CircuitBreakerPolicy());

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

           // Duende's discovery document advertises a pushed_authorization_request_endpoint, and this
           // handler's default (UseIfAvailable) switches to PAR automatically whenever that's true: the
           // real authorize parameters get POSTed to /connect/par on a back channel, and the browser
           // only ever sees "?request_uri=urn:...&client_id=...". That's invisible-by-design for a real
           // deployment, but it also means acr_values (see OnRedirectToIdentityProvider below) never
           // appears on the URL for TenantResolutionMiddleware's raw query-string parsing to find —
           // discovered by actually clicking "Log in as Acme" and watching tenant resolution silently
           // fail. Disabling PAR here keeps the classic, fully-visible query-string authorize redirect
           // this sample's simplified tenant resolution is built around.
           options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;

           options.Scope.Clear();
           options.Scope.Add("openid");
           options.Scope.Add("profile");
           // Asking for this scope is what puts an access token this app is allowed to hand to SampleApi
           // into the token response — without it, SaveTokens still stores an access token, but SampleApi
           // rejects it (no "api1" in its scope claims, so the ApiScope policy fails).
           options.Scope.Add("api1");
           // Without this, tenant_id would never show up on the claims table even for a request that
           // sets acr_values=tenant:acme — IdentityServerHost enforces the tenant match either way, but
           // it only PUTS the claim on the token if a client asked for the "tenant" scope. See
           // HomeController.LoginAsTenant() for where acr_values actually gets set.
           options.Scope.Add("tenant");

           // Keeps the id_token/access_token in the auth cookie so the Secure view below can print them.
           options.SaveTokens = true;

           // "profile" being in Scope doesn't put profile claims in the ID token by itself — Duende only
           // puts sub (and a few protocol-required claims) there for the code flow, on the assumption a
           // confidential client will ask for the rest itself. This is that ask: an automatic call to
           // /connect/userinfo after the token exchange, merging its claims into the principal.
           options.GetClaimsFromUserInfoEndpoint = true;

           options.MapInboundClaims = false;

           // The OIDC handler only merges userinfo claims it has a ClaimAction for — a pre-populated
           // allowlist covering standard OIDC claims (name, email, ...), silently dropping anything
           // else. "tenant_id" isn't standard, so without this line the userinfo endpoint would return
           // it (confirmed by calling /connect/userinfo directly) while this app's own claims table
           // never showed it — found by actually clicking "Log in as Acme" and noticing tenant_id was
           // missing even though the raw HTTP flow proved the server-side claim was there.
           options.ClaimActions.MapUniqueJsonKey("tenant_id", "tenant_id");

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

           // The OIDC handler has no built-in "AcrValues" challenge property in this version — the
           // supported way to add a parameter the handler doesn't know about is this event, which runs
           // right before the redirect to IdentityServerHost is issued. See
           // HomeController.LoginAsTenant() for where the "tenant" item actually gets set.
           //
           // Apply counterpart: Infrastructure/Authentication/Functions/OpenIdConnectFunctions.cs's
           // RedirectToIdentityProviderFunction — same two responsibilities (pick the tenant-correct
           // Authority URL, stamp acr_values=tenant:{key}), same event hook, just triggered here by an
           // explicit button click instead of being automatic on every challenge (see this project's
           // README for why).
           options.Events = new OpenIdConnectEvents
           {
               OnRedirectToIdentityProvider = context =>
               {
                   if (context.Properties.Items.TryGetValue("tenant", out var tenantKey) && tenantKey is not null)
                   {
                       var identityGatewayConfiguration = context.HttpContext.RequestServices
                           .GetRequiredService<IOptions<IdentityGatewayConfiguration>>().Value;
                       var requestUri = identityGatewayConfiguration.GetRequestUri(tenantKey);

                       context.ProtocolMessage.IssuerAddress = $"{requestUri}/connect/authorize";
                       context.ProtocolMessage.AcrValues = $"tenant:{tenantKey}";
                   }

                   return Task.CompletedTask;
               }
           };
       });

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();

// Must run after UseAuthentication() (it reads HttpContext.User) and before UseAuthorization()/endpoint
// execution (so RequireTenantAttribute and any controller code see a populated ITenantContext by the
// time they run) — same ordering constraint Apply's own UseMultitenancy() has relative to
// UseAuthentication()/UseAuthorization(), just flipped: Apply resolves tenant BEFORE authentication
// (from the hostname, so it's available for the OIDC challenge itself); this app can only resolve tenant
// AFTER authentication, because the claim it reads doesn't exist until IdentityServerHost issues it.
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();

app.MapDefaultControllerRoute();

app.Run();
