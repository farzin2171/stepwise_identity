# MvcClient — Phase 2: The MVC Client

A server-side ASP.NET Core MVC app that logs a user in through
[`../IdentityServerHost`](../IdentityServerHost) using the OpenID Connect
**Authorization Code + PKCE** flow. See that project's README for the full write-up of
Phase 2 (the new `Client` entry, the login page IdentityServer needed, and three
wire-level gotchas worth knowing) — this README covers what's specific to this side of
the flow.

This project also now carries a port of `Applications.Apply`'s (the real production MVC
BFF) multi-tenancy infrastructure and its `IdentityGatewayApi`/`ExternalServicesApi`
integration patterns — `ITenantContext`, tenant-aware login, a service-account token
client, and a config-driven external-service registry with retry/circuit-breaker
resilience. See
[`docs/multitenancy-and-external-services.md`](docs/multitenancy-and-external-services.md)
for the full, section-by-section write-up; this README's "Calling the API" and "Logging
in as a specific tenant" sections below cover the parts that predate that port and still
apply.

## Why "server-side client" matters

This app runs on a server you control, so it can hold a `ClientSecret` the browser never
sees. That's the whole distinction [`../ReactSpa`](../ReactSpa) is built to contrast
with: a React SPA runs *in* the browser, can't keep a secret, and needs a different
client configuration because of it. See its README for the full comparison.

## What's in this project

### `Program.cs`

```csharp
builder.Services.AddAuthentication(options =>
       {
           options.DefaultScheme = "cookies";
           options.DefaultChallengeScheme = "oidc";
       })
       .AddCookie("cookies")
       .AddOpenIdConnect("oidc", options =>
       {
           options.Authority = "http://localhost:5000";
           options.ClientId = "mvcclient";
           options.ClientSecret = "secret";
           options.ResponseType = "code";
           options.UsePkce = true;
           // ...
       });
```

Two authentication schemes, doing two different jobs:

- **`cookies`** holds this app's own session, once a login has completed. It's the
  `DefaultScheme` — every request checks it first.
- **`oidc`** is only used to *establish* that session. It's the `DefaultChallengeScheme`
  — the scheme ASP.NET Core redirects to when `[Authorize]` finds no valid session. Once
  the OIDC handshake finishes and the cookie is written, `oidc` doesn't run again until
  the cookie expires and a fresh challenge is needed.

A few options worth calling out specifically (the "why", not the "what" — see
`IdentityServerHost`'s README for the wire-level detail behind each):

- **`options.SaveTokens = true`** keeps the `id_token`/`access_token` in the auth cookie,
  which is how `Views/Home/Secure.cshtml` is able to print every claim in the identity.
- **`options.GetClaimsFromUserInfoEndpoint = true`** is required to see claims like
  `name` at all — the code flow's ID token alone doesn't carry them.
- **`options.CorrelationCookie`/`NonceCookie` `SameSite = Lax`** is required for the
  login to complete over plain HTTP on `localhost` — see gotcha #1 in the
  IdentityServerHost README.

### `Controllers/HomeController.cs`

```csharp
public class HomeController : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    public IActionResult Secure() => View(User.Claims);
}
```

`Index` is public. `Secure` has `[Authorize]` — the standard ASP.NET Core authorization
attribute — which is the *entire* mechanism that triggers a login: no session cookie
means no `ClaimsPrincipal`, which means the `oidc` challenge scheme fires and redirects
the browser to IdentityServerHost's `/connect/authorize`.

## Calling the API

The secure page has a **Call the API** button. It hits `HomeController.CallApi()`,
which calls [`../SampleApi`](../SampleApi) — a separate process on a separate port —
using the *same* access token this app got from IdentityServerHost during login:

```csharp
[Authorize]
public async Task<IActionResult> CallApi()
{
    var accessToken = await HttpContext.GetTokenAsync("access_token");

    var client = httpClientFactory.CreateClient("SampleApi");
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var response = await client.SendAsync(request);
    // ...
}
```

Three things make this work, none of them SampleApi-specific magic:

1. **`options.Scope.Add("api1")`** in `Program.cs` — asking for this scope during login
   is what puts an access token *good for calling SampleApi* into the token response in
   the first place. Without it, `SaveTokens` still stores *an* access token, but it
   won't carry the `api1` scope SampleApi's policy requires — the call would get a
   `401`.
2. **`options.SaveTokens = true`** — already there from Phase 2, for a different reason
   (showing tokens on the secure page). It's the same setting that makes
   `HttpContext.GetTokenAsync("access_token")` return anything at all here.
3. **`builder.Services.AddHttpClient("SampleApi", ...)`** — a named `HttpClient`
   pointed at `http://localhost:5003`. This app never validates the token itself; it
   just attaches it as a `Bearer` header and lets SampleApi do that work independently.

This is the same pattern the real IdG's clients use to call the real IdG's protected
APIs — a client that already has a user's access token from login reuses it, rather
than asking for a *new* token per downstream call.

The secure page now has a **second** button, *Call the API (as the service account)*,
hitting `HomeController.CallApiAsServiceAccount()` — same endpoint, same
`HttpClient`, but authenticated with a client-credentials token fetched via
`ITokenClient` instead of the signed-in user's own token. No user is involved in that
call at all, and SampleApi's response shows it: no `sub`, no `name`, no `email` — just
`client_id: mvcclient-svc.acme`. See
[`docs/multitenancy-and-external-services.md`](docs/multitenancy-and-external-services.md#3-externalservicesapi)
for the full write-up of both patterns side by side, and for where
`ExternalServicesApi`'s config-driven `ServiceDefinition` registry (which now supplies
this `HttpClient`'s base address) and its Polly retry/circuit-breaker policies come from.

[`../ReactSpa`](../ReactSpa) has the same button and calls the same endpoint, but
notably **doesn't** need `AddHttpClient`, `GetTokenAsync`, or any server-side code at
all — it just calls `fetch()` directly from the browser with the token already sitting
in `sessionStorage`. It also needed something this app never did: a CORS policy on
SampleApi itself, because that call crosses origins (`:5173` → `:5003`) in a way this
app's server-to-server call never does. See its README for the comparison.

## Running it

See [`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it) —
all three projects need to be running together for any of them to make sense on its own.

Quick version:

```bash
# terminal 1
cd ../IdentityServerHost && dotnet run

# terminal 2
cd . && dotnet run --urls http://localhost:5002

# terminal 3
cd ../SampleApi && dotnet run --urls http://localhost:5003
```

Then browse to `http://localhost:5002` and try either link:

- *Go to the secure page* — no tenant hint, works for any local user.
- *Log in as Acme Corp* — sign in as `alice`/`alice` (succeeds, `tenant_id: acme` on the
  claims table, **Tenant (from `ITenantContext`): Acme Corp (acme)** shown above it) or
  via the ExternalIdp button as `carol`/`carol` (needs `ExternalIdp` running too — see
  [`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it)).
  Try `bob`/`bob` here to see the tenant-mismatch rejection.
- *Log in as Globex Corporation* — sign in as `bob`/`bob` (succeeds); try `alice`/`alice`
  here instead for the same rejection from the other direction.

Once signed in, try both API buttons — *Call the API (as me)* and *Call the API (as the
service account)* — and compare the two responses (see "Calling the API" below).

Prefer not to click through a browser? [`test-api.ps1`](../../test-api.ps1) (repo root)
drives the forwarded-user-token login + API call over raw HTTP;
[`test-multitenancy-external-services.ps1`](../../test-multitenancy-external-services.ps1)
drives the tenant-context and service-account additions.

## Logging in as a specific tenant (Phase 3)

IdentityServerHost resolves and enforces a tenant per login from the OIDC
`acr_values=tenant:<name>` request parameter (see its README's "Phase 3" section) — but
a client has to actually *set* that parameter before redirecting for any of it to kick
in. `[Authorize]` on `Secure()` alone never does — it triggers a bare challenge with no
tenant hint, so IdentityServerHost's login page defaults to local-login-only, same as
before Phase 3 existed. The home page's **Log in as Acme Corp** / **Log in as Globex
Corporation** links exist specifically to set it:

```csharp
[HttpGet]
public IActionResult LoginAsTenant(string tenant)
{
    var props = new AuthenticationProperties
    {
        RedirectUri = Url.Action(nameof(Secure)),
        Items = { ["tenant"] = tenant }
    };
    return Challenge(props, "oidc");
}
```

Two things worth knowing if you go looking for `OpenIdConnectChallengeProperties.AcrValues`
— it doesn't exist in this package version. The supported way to add a parameter the
handler has no dedicated property for is an event hook, registered in `Program.cs`:

```csharp
options.Events = new OpenIdConnectEvents
{
    OnRedirectToIdentityProvider = context =>
    {
        if (context.Properties.Items.TryGetValue("tenant", out var tenantKey) && tenantKey is not null)
        {
            var identityGatewayConfiguration = context.HttpContext.RequestServices
                .GetRequiredService<IOptions<IdentityGatewayConfiguration>>().Value;
            context.ProtocolMessage.IssuerAddress = $"{identityGatewayConfiguration.GetRequestUri(tenantKey)}/connect/authorize";
            context.ProtocolMessage.AcrValues = $"tenant:{tenantKey}";
        }
        return Task.CompletedTask;
    }
};
```

`context.Properties` here is the same `AuthenticationProperties` object `Challenge(props, "oidc")`
was called with — `LoginAsTenant` stashes the raw tenant key in `Items`, and this event
reads it back out right before the redirect to IdentityServerHost is built. It now does
two things with that key, not one: builds the `acr_values` hint (as before), and picks
the tenant-correct Authority URL via `IdentityGatewayConfiguration.GetRequestUri` — see
[`docs/multitenancy-and-external-services.md`](docs/multitenancy-and-external-services.md#a-tenant-aware-oidc-redirect)
for what that's for.

### The PAR gotcha this surfaced

The first version of this genuinely didn't work, for a reason worth knowing: Duende's
discovery document advertises a `pushed_authorization_request_endpoint`, and this OIDC
handler's default (`PushedAuthorizationBehavior.UseIfAvailable`) switches to **Pushed
Authorization Requests (PAR, RFC 9126)** automatically whenever a server supports it.
Under PAR, the handler POSTs the real authorize parameters — including `acr_values` — to
`/connect/par` on a back channel, and the browser's actual redirect only ever carries
`?request_uri=urn:...&client_id=...`. IdentityServerHost's `TenantResolutionMiddleware`
does deliberately simple query-string parsing (see its README), so it never saw
`acr_values` at all under PAR — silently landing on local-login-only, no error anywhere.
Fixed with:

```csharp
options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
```

This keeps the classic, fully query-string-visible authorize redirect this sample's
simplified tenant resolution is built around. A real production client would likely want
PAR's actual benefit (authorize parameters never sit in browser history/logs) and would
instead need `TenantResolutionMiddleware` to resolve tenant the way the *real* IdG does —
from Duende's own parsed `AuthorizationRequest`, not a raw query string.

### A second gotcha, one layer deeper: claims that don't merge

Even with the tenant hint reaching IdentityServerHost correctly, `tenant_id` still didn't
show up on this app's own claims table — despite the exact same claim showing up when
`/connect/userinfo` was called directly with curl. The cause: this OIDC handler only
merges userinfo claims that have a registered `ClaimAction`, and `ClaimActions` ships
pre-populated with mappings for standard OIDC claims (`name`, `email`, ...) but nothing
for a custom claim like `tenant_id` — it gets silently dropped, no warning, no error.
Fixed with one more line:

```csharp
options.ClaimActions.MapUniqueJsonKey("tenant_id", "tenant_id");
```

Any custom (non-standard) claim a real deployment wants surfaced through
`GetClaimsFromUserInfoEndpoint` needs an explicit line like this — this is not specific
to `tenant_id`.

## About external IdP federation (Phase 4)

IdentityServerHost can now federate Acme's users to [`../ExternalIdp`](../ExternalIdp)
(see its README's "Phase 4" section) — and this app needed **zero changes** to benefit
from that. From this app's point of view, a user who signed in via ExternalIdp looks
exactly like one who typed a local password: same `/connect/authorize` →
`/connect/token` exchange, same claims on the secure page. The federation happens
entirely behind IdentityServerHost's own login page, which is the whole point of the
OIDC client/provider boundary — this app only ever talks to *one* identity provider
(IdentityServerHost), regardless of how many identity providers *that* federates to
behind the scenes.
