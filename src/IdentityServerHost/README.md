# IdentityServerHost

This is the authorization server half of a mini "Identity Gateway" built from scratch,
one phase at a time, so that each phase is a small, runnable slice of what a real
OAuth/OIDC authorization server (like Equisoft's `Applications.IdentityGateway`)
actually is under all its production scaffolding.

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ✓
4. External identity providers ✓
5. Persistence (SQL Server instead of in-memory) ✓
6. Data ingestion / config tooling ✓
7. DIT external-service calls (TenantClient, UserClient) ✓
8. Signing-key management (Key Vault instead of a developer credential) ✓
9. IdentityProviderStore (DB-persisted external-provider config) ← next
```

The sibling projects [`../MvcClient`](../MvcClient) and [`../ReactSpa`](../ReactSpa) are
the other half of Phase 2 — two applications, of the two kinds the target architecture
actually has (a server-side app and a browser-only SPA), that log a user in against this
server. [`../ExternalIdp`](../ExternalIdp) (Phase 4) is different in kind: a second,
independent Duende IdentityServer this project federates *to*, not a client of it. Each
sibling's own README covers what it is; this one covers the IdentityServer side of every
flow.

## Phase 1 recap — why start with (almost) nothing?

A real `Startup.cs` wires `AddIdentityServer()` in the middle of a much bigger pipeline —
SAML2, data protection, audit logging, metrics, message queues, health checks, resilient
HTTP clients, and more — all built around six phases like the ones above. None of that
scaffolding changes what IdentityServer actually *is*. Phase 1 stripped everything else
away so the load-bearing part is visible on its own:

> **IdentityServer is middleware.** One call registers its services. One call adds it to
> the request pipeline.

Everything added from Phase 2 onward (login pages, multi-tenant resolution, external
IdP federation, real databases) is a *customization* layered on top of those two calls —
not a prerequisite for them.

### `Config.cs` — the shape of configuration

```csharp
public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile()
    ];

    public static IEnumerable<ApiScope> ApiScopes => [];

    public static IEnumerable<Client> Clients => [ /* see Phase 2 below */ ];
}
```

- **`IdentityResources`** map to OIDC scopes that describe *who the user is*. `OpenId` is
  the mandatory `openid` scope every OIDC request needs. `Profile` is the standard
  `profile` scope (name, picture, etc.).
- **`ApiScopes`** describe *APIs* this server protects (e.g. `orders.read`). Still empty
  — nothing to protect until a later phase.
- **`Clients`** describe *applications allowed to ask this server for tokens*. Phase 1
  left this empty on purpose; Phase 2 adds the first one.

In a real IdG, these three lists aren't a static C# class — they're rows in SQL Server,
seeded from JSON config files through a data-ingestion tool (Phase 6 territory). But the
*objects* are exactly the same types: `Duende.IdentityServer.Models.IdentityResource`,
`ApiScope`, and `Client` don't know or care whether they came from memory or a database.
That's the whole point of Duende's store abstraction.

### `Program.cs` — the two calls that matter

- **`AddIdentityServer(...)`** registers IdentityServer's services in the DI container —
  token validators, response generators, and the stores you configure via
  `.AddInMemory...()`.
- **`UseIdentityServer()`** adds IdentityServer's middleware to the HTTP request
  pipeline. This is what actually answers requests to
  `/.well-known/openid-configuration`, `/connect/authorize`, `/connect/token`, and every
  other OIDC/OAuth endpoint. You never write these endpoints yourself.

`AddDeveloperSigningCredential()` is the one line with no production equivalent — it
writes a throwaway RSA signing key to disk (`tempkey.jwk`, gitignored) and reuses it on
subsequent runs. A real IdG calls `AddCertificates()` instead, loading a real
certificate from a key vault.

---

## Phase 2 — the first client, and the login page it needed

Phase 1 built an IdentityServer with nothing to authenticate *to*: zero clients means
every real OAuth flow rejects every request, because there's no `client_id` that could
ever be valid. Phase 2 gives it exactly one client — `MvcClient`, a server-side ASP.NET
Core MVC app — and traces one real, complete **Authorization Code + PKCE** flow through
both applications.

```
Browser  ↔  MvcClient (:5006)  —code + PKCE→  IdentityServerHost (:5001)
```

**MvcClient is a "server-side client"** — it runs on a server you control, so it can
hold a `ClientSecret` the browser never sees. That distinction matters again in the next
phase: a React SPA runs *in* the browser, can't keep a secret, and needs a different
client configuration because of it.

### `Config.cs` — the new client

```csharp
new Client
{
    ClientId = "mvcclient",
    ClientSecrets = { new Secret("secret".Sha256()) },

    AllowedGrantTypes = GrantTypes.Code,
    RequirePkce = true,
    RequireConsent = false,

    RedirectUris = { "https://localhost:5006/signin-oidc" },
    PostLogoutRedirectUris = { "https://localhost:5006/signout-callback-oidc" },

    AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile }
}
```

`RequirePkce = true` even though this client has a secret. PKCE was designed for
*public* clients that can't authenticate themselves — but it also closes a second,
unrelated hole (authorization-code interception on the redirect back to the client) that
affects confidential clients too. Duende — and the real IdG — require it on every client
unconditionally. There's no `RedirectUris` wildcard and no dynamic registration: this
list is exactly one URL, matching the real IdG's philosophy that redirect URIs are a
security boundary, not a convenience setting.

### The login page IdentityServer needed

Duende IdentityServer ships zero UI. `IIdentityServerInteractionService` is the seam:
when a `/connect/authorize` request can't be completed (no session yet), IdentityServer
redirects to whatever URL `options.UserInteraction.LoginUrl` points at (default:
`/Account/Login`) and trusts your application code to authenticate the user and call
back in. That's what `Controllers/AccountController.cs`, `TestUsers.cs`, and
`Views/Account/Login.cshtml` are for:

```csharp
if (users.ValidateCredentials(model.Username, model.Password))
{
    await HttpContext.SignInAsync(new IdentityServerUser(user.SubjectId)
    {
        DisplayName = user.Username,
        IdentityProvider = IdentityServerConstants.LocalIdentityProvider
    });

    // Resumes the /connect/authorize request that redirected here in the first place —
    // signing in above didn't finish the OIDC flow, it just made this redirect valid.
    if (Url.IsLocalUrl(model.ReturnUrl)) return Redirect(model.ReturnUrl);
    return Redirect("~/");
}
```

`TestUserStore` and `TestUser` are Duende's own quickstart types — a real password check
against a hard-coded list (`alice`/`alice`, `bob`/`bob`), wired up by
`.AddTestUsers(TestUsers.Users)` in `Program.cs`. **The real IdG has no code path like
this at all** — no local password login exists there; every login goes to an external
IdP (that's Phase 4). This local-password UI is scaffolding this course needs and the
real system doesn't.

### Three things that broke, and why they're worth knowing

These are real wire-level behaviors, reproduced by actually running this sample — not
bugs, things worth understanding about how OIDC actually works over the wire.

1. **"Correlation failed" — `SameSite=None` requires `Secure`, which requires HTTPS.**
   The OIDC handler's correlation and nonce cookies default to `SameSite=None` because
   IdentityServer's callback is a cross-origin `POST` back into the app (see #2) — and
   every modern browser refuses a `SameSite=None` cookie that isn't also marked
   `Secure`. This sample runs both apps over plain HTTP, so a `Secure` cookie set on the
   way out never came back on the way in, and login failed before it reached the login
   page's credential check. **Fix:** relax both `CorrelationCookie` and `NonceCookie` (on
   the MvcClient side) — and IdentityServer's own cookies (`ConfigureAll<CookieAuthenticationOptions>`
   in `Program.cs`, IdentityServerHost side) — to `SameSite=Lax`. Safe specifically
   because `localhost:5000` and `localhost:5002` are *same-site* (SameSite is defined by
   scheme + registrable domain, not port). A real deployment with the IdG and its
   clients on different domains would need real HTTPS instead of this relaxation —
   there'd be no substitute.
2. **`response_mode=form_post` is the library default, and it's a real cross-origin
   POST.** ASP.NET Core's OpenIdConnect handler defaults to `form_post`: IdentityServer's
   authorize *callback* response is an HTML page containing a self-submitting
   `<form method="post">` that JavaScript fires onload, POSTing the code and state to
   `/signin-oidc`. The alternative, `response_mode=query`, would put the same code in a
   URL — sitting in browser history and any `Referer` header the next page load sends.
   `form_post` avoids that at the cost of needing JavaScript to complete the round trip.
3. **`profile` in scope ≠ profile claims in the ID token.** For the code flow, Duende
   puts only `sub` and a few protocol-required claims into the ID token — it expects a
   confidential client to fetch the rest itself. `Alice Anderson` doesn't show up on the
   secure page until `options.GetClaimsFromUserInfoEndpoint = true` is set on the
   MvcClient's OIDC options, triggering an automatic call to `/connect/userinfo` after
   the token exchange. The alternative is `AlwaysIncludeUserClaimsInIdToken = true` on
   the `Client` — fewer round trips, bigger tokens whether the claims get used or not.

---

## ReactSpa — the second client type, and why it looks different

`MvcClient` is a **confidential** client: it runs on a server you control, so it can
hold a `ClientSecret` the browser never sees. [`../ReactSpa`](../ReactSpa) is the other
kind the target architecture has — a **public** client, static files a browser
downloads and runs, with no server of its own to keep a secret on. Same protocol
(Authorization Code + PKCE), same IdentityServer, same two identity claims at the end —
but everything *different* about it traces back to that one fact.

### `Config.cs` — the public client

```csharp
new Client
{
    ClientId = "reactspa",
    RequireClientSecret = false,

    AllowedGrantTypes = GrantTypes.Code,
    RequirePkce = true,

    RedirectUris = { "http://localhost:5173/callback" },
    AllowedCorsOrigins = { "http://localhost:5173" },

    AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile, "api1" }
}
```

Two fields `mvcclient` never needed:

- **`RequireClientSecret = false`** — there's nothing to authenticate the client itself
  with, because a public client can't keep anything confidential. PKCE alone protects
  the authorization code exchange here — for `mvcclient`, PKCE was defense in depth *on
  top of* a secret; for `reactspa`, it's the only defense that exists.
- **`AllowedCorsOrigins`** — IdentityServer reads this and wires up CORS for every one
  of its endpoints automatically. Without it, the browser's preflight `OPTIONS` request
  to `/connect/token` gets no `Access-Control-Allow-Origin` header back, and the actual
  `POST` never leaves the browser at all. This is the gotcha every SPA-onboarding
  conversation about a real IdG runs into first.

See `ReactSpa`'s own README for the full MvcClient-vs-ReactSpa comparison (client type,
where tokens end up living, who calls `/connect/token`) and for what the React side of
this looks like.

---

## SampleApi — protecting an API with the same tokens

Everything so far has been about the *issuer* side: this server produces tokens, and
MvcClient consumes one to establish a login. [`../SampleApi`](../SampleApi) adds the
third role in the triangle — a **resource server** that receives one of those tokens
from MvcClient (not from a fresh login of its own) and independently decides whether to
trust it. See `SampleApi`'s own README for how it validates a token; this section covers
what changed here to make that possible.

### `Config.cs` — an `ApiScope` and an `ApiResource`

```csharp
public static IEnumerable<ApiScope> ApiScopes =>
[
    new ApiScope("api1", "Sample API access")
];

public static IEnumerable<ApiResource> ApiResources =>
[
    new ApiResource("api1", "Mini IdG Sample API")
    {
        Scopes = { "api1" },
        UserClaims = { "name", "email", "tenant_id" }
    }
];
```

- **`ApiScope("api1", ...)`** is what a client (`mvcclient`, below) asks for in
  `AllowedScopes`/`Scope` to get a token usable against SampleApi.
- **`ApiResource("api1", ...)`** is what turns that scope name into the token's `aud`
  (audience) claim. Without an `ApiResource`, Duende issues access tokens with **no**
  `aud` claim at all — there'd be nothing for an API's `ValidAudience` check to compare
  against. This is a common early-Duende-adopter trap: adding only an `ApiScope` and
  wondering why the API rejects every token.
- **`UserClaims = { "name", "email", "tenant_id" }`** — by default an access token
  carries only protocol claims (`sub`, `scope`, `client_id`, ...), *not* the identity
  claims that ended up in the ID token via the `profile`/`tenant` scopes. An access
  token and an ID token don't automatically share claims; this list is what copies
  `name`/`email`/`tenant_id` onto the access token too. `tenant_id` was added for
  SampleApi's own `IIdentityContext` port — without it here, `tenant_id` reached
  MvcClient's ID token (and its own `ITenantContext`) but never SampleApi's access
  token at all. See
  [`../SampleApi/docs/identity-context-and-conventions.md`](../SampleApi/docs/identity-context-and-conventions.md)
  for what SampleApi does with it.

### `Config.cs` — both clients now ask for `api1`

```csharp
AllowedScopes =
{
    IdentityServerConstants.StandardScopes.OpenId,
    IdentityServerConstants.StandardScopes.Profile,
    "api1"
}
```

`AllowedScopes` on a `Client` is an allowlist — it says what that client is *permitted*
to request, not what it *does* request. Both `mvcclient`'s `Program.cs` and
`ReactSpa/src/main.tsx` have to separately add `"api1"` to their own scope list for a
token to actually come back with it. Two places, two different jobs: IdentityServerHost
decides what's allowed *per client*; each client decides what it asks for on any given
login. `reactspa` got this added after the fact, once it grew its own *Call the API*
button — see `ReactSpa`'s README.

### `Program.cs` — one more in-memory store

```csharp
.AddInMemoryApiScopes(Config.ApiScopes)
.AddInMemoryApiResources(Config.ApiResources)
```

Same pattern as every other `.AddInMemory...()` call in this file — a real IdG would
call `.AddApiResources()`/`.AddApiScopes()` against the same SQL-backed configuration
store as everything else (Phase 5 territory), not a different mechanism.

### `Config.cs` — two more clients, with no user involved at all

```csharp
new Client
{
    ClientId = "mvcclient-svc.acme",
    ClientSecrets = { new Secret("acme-svc-secret".Sha256()) },
    AllowedGrantTypes = GrantTypes.ClientCredentials,
    AllowedScopes = { "api1" }
},
new Client
{
    ClientId = "mvcclient-svc.globex",
    ClientSecrets = { new Secret("globex-svc-secret".Sha256()) },
    AllowedGrantTypes = GrantTypes.ClientCredentials,
    AllowedScopes = { "api1" }
}
```

Added when MvcClient ported `Applications.Apply`'s service-account token pattern — see
[`MvcClient/docs/multitenancy-and-external-services.md`](../MvcClient/docs/multitenancy-and-external-services.md).
Every other client in this file uses `GrantTypes.Code` — a human logs in, a browser is
involved, tokens come back with a `sub`. `GrantTypes.ClientCredentials` is different in
kind, not just configuration: **no user, no browser, no redirect** — just a direct
server-to-server POST to `/connect/token` trading a client secret for a token. Two
clients, one per tenant, each with its own secret, is the load-bearing detail: it means
revoking or rotating Acme's service-account access can never accidentally affect
Globex's.

---

## Phase 3 — tenant resolution

Every login so far has been "some user, some client" — nothing has cared *which
organization* that user belongs to. Phase 3 adds a second dimension: `alice` belongs to
Acme Corp, `bob` belongs to Globex Corporation, and a login can now say up front which
tenant it's for — and get rejected if the credentials that come back don't match.

### The signal: `acr_values=tenant:<name>`

The real IdG's own code documents this convention in a log message
(`AuthenticationHelper.cs`): *"Make sure that the request has the parameter 'acr_values'
set with property 'tenant:name_of_tenant'."* `acr_values` is a standard OIDC request
parameter (Authentication Context Class Reference, normally used to request a
particular authentication strength) that Duende additionally special-cases a
`tenant:`-prefixed entry inside. Nothing about a client's *static* configuration says
"this request is for Acme" — that has to arrive with the request, because the same
client (`reactspa`, `mvcclient`) can serve users from either tenant.

### Where this sample simplifies

Your instinct might be to look for "the tenant resolution component" in the real IdG.
There isn't one — tenant gets re-derived independently in at least three places there.
Building an actual middleware here is a deliberate simplification, not a mirror:

| Question | Real IdG | This sample |
|---|---|---|
| Which IdPs/branding to show at login? | `AuthenticationHelper.GetAllAvailableIdentityProviders` reads `context.Tenant` + a per-client `Properties[tenantName]` entry | `TenantResolutionMiddleware` populates `TenantContext`, read by `AccountController` |
| Does an authenticated user's tenant match what was requested? | `EquisoftAuthorizeInteractionResponseGenerator`, at **token-issuance** time — forces re-login unless tenants are configured as `LinkedTenants` | `AccountController.Login`, at **credential-submission** time — a hard reject, no linking concept |
| What tenant does an external-IdP scheme belong to? | `ITenantAccessor` — scheme name → `EcosystemTenant` config value | N/A — no external IdP yet (Phase 4) |
| What gets stamped into the token? | `EquisoftTokenResponseGenerator` stamps `tenantId` into **every** token, unconditionally | An opt-in `tenant` scope — a client has to ask for it |

Three components, three different signals (a scheme name, an acr value, a claim),
collapsed into one middleware and one scope here. Fair trade for a teaching sample — the
concept is visible in one place — but this code is not something you could lift into
the real IdG's PR queue.

### `Tenants.cs`, `TenantContext.cs`, `TenantResolutionMiddleware.cs`

```csharp
public async Task InvokeAsync(HttpContext context, TenantContext tenantContext)
{
    var tenantKey = Tenants.ResolveTenantKey(context.Request.GetEncodedPathAndQuery());
    if (tenantKey is not null)
    {
        tenantContext.TenantKey = tenantKey;
        tenantContext.DisplayName = Tenants.DisplayNames[tenantKey];
    }
    await next(context);
}
```

The one wrinkle `Tenants.ResolveTenantKey` has to handle: `acr_values` is a direct query
parameter on `/connect/authorize`, but by the time IdentityServer has redirected to
`/Account/Login?ReturnUrl=...`, it isn't a top-level parameter anymore — Duende
re-encodes the *entire original request* inside `ReturnUrl` and hands that to the login
page instead. `ResolveTenantKey` checks both shapes, which is exactly what the real
`AuthenticationHelper` gets for free by asking
`IIdentityServerInteractionService.GetAuthorizationContextAsync(returnUrl)` instead of
parsing raw query strings — a real API this sample deliberately avoids so the underlying
convention stays visible.

`TenantResolutionMiddleware` runs after `UseRouting()` (so it only fires for requests
that will actually be handled) and before `UseIdentityServer()`, so both
`/connect/authorize` and `/Account/Login` see a populated `TenantContext` by the time
their handlers run. `TenantContext` itself is registered `AddScoped<TenantContext>()` —
one instance per request, written once by the middleware, read later by
`AccountController` in the same request.

### Enforcement — `AccountController.cs`

```csharp
var requiredTenant = Tenants.ResolveTenantKey(model.ReturnUrl);
var usersTenant = user.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
if (requiredTenant is not null && usersTenant != requiredTenant)
{
    ModelState.AddModelError(string.Empty,
        $"{model.Username} does not belong to {Tenants.DisplayNames[requiredTenant]}.");
    return View(...);
}
```

Alice belongs to Acme. If a request arrives with `acr_values=tenant:globex` and Alice
types her correct password, she's still rejected — right password, wrong tenant. This
is the concrete version of the real system's mismatch check; the real one runs later
(after signing in, at the point IdentityServer is about to issue a token) and has an
escape hatch (`LinkedTenants`) this sample doesn't implement.

> **Known real-system caveat, carried forward and reproduced in Phase 7:** the actual
> tenant GUID lookup (`TenantClient.GetTenantAsync`) is cached with `AbsoluteExpiration =
> DateTimeOffset.MaxValue` — it never expires. A tenant's GUID changing in the source of
> truth would not be picked up without an app restart. `Tenants.cs` here is still a
> hard-coded dictionary with no cache of its own — it resolves *which* tenant, not the
> tenant's GUID — but Phase 7's `TenantClient`/`SampleProfileService` now reproduce this
> exact bug for the GUID lookup itself. See Phase 7's section below.

### `Config.cs` — an opt-in `tenant` scope

```csharp
new IdentityResource { Name = "tenant", DisplayName = "Tenant", UserClaims = { "tenant_id" } }
```

Added to both `mvcclient`'s and `reactspa`'s `AllowedScopes`. Same pattern as `api1` in
the SampleApi section above: this is an allowlist, not a request — a client still has
to put `"tenant"` in its own `Scope`/`scope` list for `tenant_id` to actually show up
anywhere.

---

## Phase 4 — external identity providers, per tenant

The deep mechanics of external federation — claim mapping, first-login provisioning
bugs, `FederatedConfiguration` — are a whole separate topic covered against the *real*
IdG elsewhere in this course. This phase deliberately doesn't re-teach that. Its job is
narrower and specific to this sample's brief: **which external IdP a login page offers
depends on the tenant**, built on top of Phase 3's `TenantContext` rather than in
isolation.

```
Browser  ↔  IdentityServerHost (:5001 — mini-idg)  ↔ (Acme only) ↔  ExternalIdp (:5011 — a separate org)
```

[`../ExternalIdp`](../ExternalIdp) knows nothing about tenants — it's a plain, second,
independent Duende IdentityServer with one test user (Carol) and one registered client
(mini-idg itself). From ExternalIdp's point of view, this project is *just another
relying party*. Everything tenant-aware lives entirely on this side.

### Per-tenant gating — config-driven, not hardcoded

Phase 4's first cut hardcoded a `Tenants.AllowedExternalSchemes` dictionary. That's gone
now — see [`docs/external-providers-configuration.md`](docs/external-providers-configuration.md)
for the full write-up of what replaced it and why. Short version: each provider
declares its own tenant in config —

```json
// appsettings.Development.json
"ExternalProviders": { "OpenId": [ { "Name": "external-idp", "EcosystemTenant": "acme", "...": "..." } ] }
```

— and `AuthenticationHelper.GetAllAvailableIdentityProviders(tenantKey)` filters the
whole provider list by that field, instead of a second hand-maintained mapping.
`AccountController.Login` calls that and hands the result to the view as
`ExternalProviders`. Acme's login page shows a **Sign in with ExternalIdp** button above
the local form; Globex's shows only the form — the actual HTML the server renders
differs by tenant, not just a label somewhere.

### `Controllers/ExternalController.cs` — challenge and callback

Every external scheme (just `"external-idp"` here; a real deployment might have several)
converges on the same two actions:

```csharp
public IActionResult Challenge(string scheme, string returnUrl)
{
    var props = new AuthenticationProperties
    {
        RedirectUri = Url.Action(nameof(Callback)),
        Items =
        {
            ["returnUrl"] = returnUrl,
            // ExternalIdp has no concept of "acme" - this is the one piece of local
            // context that has to survive the round trip, same mechanism as returnUrl.
            ["tenant"] = tenantContext.TenantKey
        }
    };
    return base.Challenge(props, scheme);
}
```

`Items` round-trips through the encrypted `state` parameter automatically —
`returnUrl` surviving an external hop is the standard mechanism every OIDC quickstart
relies on; `tenant` riding along in the same dictionary is this sample's addition, and
the reason Carol ends up with `tenant_id: acme` even though ExternalIdp never heard the
word "acme."

### Four things that broke, and what each one actually teaches

1. **IdentityServer's own cookies default to `SameSite=None` without `Secure` — and this
   is a hard browser rejection, not just a logged warning.** The framework logs a
   warning (`"idsrv.external" has set 'SameSite=None' and must also set 'Secure'`), but
   the real consequence is worse than the log line suggests: a real browser refuses to
   *store* a `SameSite=None` cookie sent without `Secure` at all, full stop. Same shape
   as Phase 2's correlation-cookie problem, just on *IdentityServer's own* session
   cookies instead of the OIDC client handler's — fixed here the same way, with the
   blanket `ConfigureAll<CookieAuthenticationOptions>(...)` this project added in Phase 2.
   **That fix does not cover `idsrv.session`, though**, despite this README previously
   claiming otherwise — see the dedicated note below, and the real bug this caused.
   **And it has to be applied separately to every project that's its own Duende
   IdentityServer.** `../ExternalIdp` is a second, completely independent ASP.NET Core
   app with its own `Program.cs` — the fix here does nothing for it. Missing it there
   was a real, confirmed bug (not a documentation nitpick): traced with a raw
   `HttpClient` that doesn't enforce cookie policy, the federated login always
   succeeded; traced with the browser's actual `Secure`/`SameSite` rules in mind
   (inspecting the literal `Set-Cookie` headers), ExternalIdp's own `idsrv` cookie came
   back `samesite=none` with no `Secure`, over plain HTTP — a real browser would drop it
   outright, and Carol's sign-in on ExternalIdp would never survive the redirect back
   into ExternalIdp's *own* `/connect/authorize/callback`. Fixed by adding the same
   `ConfigureAll<CookieAuthenticationOptions>(...)` call to `ExternalIdp/Program.cs`.
   **The concrete lesson:** a scripted HTTP-client test proves your *logic* is correct;
   it does not prove your cookies survive a real browser's policy enforcement. Both are
   needed, and neither substitutes for the other.

   > **The `idsrv.session` cookie needs a *different* fix, in a *different* place.**
   > `ConfigureAll<CookieAuthenticationOptions>` only touches cookies written through
   > ASP.NET Core's standard cookie-authentication handler. `idsrv.session` isn't one of
   > those — it's written directly by Duende's own session-management service, for the
   > cross-origin check-session-iframe feature (which defaults its `SameSite` to `None`
   > for exactly that reason, and which neither MvcClient nor ReactSpa implements).
   > Relaxing it takes a separate, dedicated setting:
   > `options.Authentication.CheckSessionCookieSameSiteMode = SameSiteMode.Lax;` inside
   > `AddIdentityServer(options => { ... })` — in **both** `IdentityServerHost/Program.cs`
   > and `ExternalIdp/Program.cs`. The takeaway generalizes beyond this one cookie: not
   > every cookie a framework sets goes through the options object you'd naturally reach
   > for first, and "I called the blanket fix" isn't the same as verifying every cookie
   > actually changed — which is exactly what inspecting raw `Set-Cookie` headers, not
   > just trusting a passing test, caught here.
2. **`response_mode=form_post` cascades — there isn't just one auto-post form in a
   federated login, there are two.** One from ExternalIdp's own authorize callback
   (handing the code back to mini-idg), and — because mini-idg's *own* pending authorize
   request for the React SPA is still in flight underneath — a second one appears
   later, at the very end, handing the final code to `reactspa`. Federation doesn't
   replace the outer OIDC flow; it happens inside a pause in it.
3. **The profile service's `context.Subject` is not the principal you signed in with.**
   The real find. An early version of `ExternalController.Callback` put `name` and
   `tenant_id` onto the principal directly via `IdentityServerUser.AdditionalClaims`,
   then a plain pass-through profile service tried to read them back from
   `context.Subject.Claims` at token-issuance time — and got only `sub`. IdentityServer
   reconstructs `context.Subject` from the session as a minimal principal; whatever you
   signed in with beyond the protocol-required claims doesn't ride along for free. The
   fix (`ExternalUserStore.cs`) isn't a workaround — it's the same shape as the real
   system's answer: **persist what you provisioned, and have the profile service look it
   up.** The naive approach would have taught the wrong mental model even though it
   happened to compile.
4. **Every external provider is itself a Duende IdentityServer, so it advertises Pushed
   Authorization Requests (PAR) too — and this hop needed the same fix MvcClient did.**
   Same underlying gotcha as MvcClient's README's "The PAR gotcha this surfaced," one hop
   further out: IdentityServerHost's own OIDC client registration for `"external-idp"`
   (in `Configurations/Authentication/OpenId/OpenIdConnectAuthenticationExtensions.cs`)
   defaults to using PAR against ExternalIdp automatically, which replaces the visible
   `/connect/authorize?client_id=...&scope=...` query string with an opaque
   `?request_uri=urn:...&client_id=...`. Duende's default PAR lifetime is 10 minutes and
   a scripted trace completes through it without issue either way, so this specific hop
   wasn't confirmed to be *breaking* anything — but it hides every parameter this
   sample's whole design deliberately keeps visible, and it's the exact same class of
   surprise MvcClient's own PAR fix was for. Found by watching an actual login trace and
   asking "why does this URL look different from every other authorize redirect in this
   repo?" Fixed the same way:
   `options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;`.

One more piece worth naming: the default profile service `.AddTestUsers()` registers
only knows how to answer "is this user active?" for subjects in `TestUsers.Users` — it
silently rejects Carol ("User is not active," no further detail in the log).
`Services/SampleProfileService.cs` replaces it, branching on the `idp` claim to ask
either `TestUserStore` (local) or `ExternalUserStore` (federated).

### Registering the federation — config-driven

```csharp
// Program.cs
builder.Services.AddAuthentication()
       .AddExternalProvidersFromFile(builder.Configuration);
```

That one line replaces what used to be a hardcoded `.AddOpenIdConnect("external-idp",
options => { ... })` block in `Program.cs`. It loops every entry under the
`ExternalProviders` config section and calls `AddOpenIdConnect` once per provider — see
[`Configurations/Authentication/ExternalProviderAuthenticationExtensions.cs`](Configurations/Authentication/ExternalProviderAuthenticationExtensions.cs)
and [`docs/external-providers-configuration.md`](docs/external-providers-configuration.md)
for the full shape. Inside that per-provider registration
(`Configurations/Authentication/OpenId/OpenIdConnectAuthenticationExtensions.cs`),
`SignInScheme = ExternalCookieAuthenticationScheme` is the one line doing the real
work — without it, a successful ExternalIdp login would write this app's *main* session
cookie directly and the user would just be logged in, bypassing
`ExternalController.Callback` (and its tenant-matching, provisioning, and
local-session-issuing logic) entirely. Pointing it at the *external* cookie instead
makes the ExternalIdp result a short-lived, intermediate fact that the callback still
has to convert into a real session.

Adding a second provider — a real Entra ID tenant, say — is now a config change, not a
code change: see [`docs/azure-entra-b2c-setup.md`](docs/azure-entra-b2c-setup.md). Wiring
one up for real also surfaced a "Correlation failed" chain worth understanding in detail
— a managed-browser policy blocking cookies over plain HTTP, the HTTPS migration that
forced across every project in the solution, and a `SameSite=Lax` vs. cross-site
`form_post` issue underneath it — see
[`docs/correlation-failed-troubleshooting.md`](docs/correlation-failed-troubleshooting.md).

### What's deliberately still a simplification

- **Tenant is a property of the request here**, resolved from `acr_values` and carried
  through `AuthenticationProperties`. A real system's equivalent typically resolves it
  from the *scheme name* instead (a static per-provider config value) — a real
  difference in shape, not just a missing feature, as the Phase 3 section above already
  flagged.
- **No claim-mapping complexity.** No `FederatedConfiguration`, no
  `ExternalIdClaimName`, no duplicate-claim collision handling. See
  [`docs/azure-entra-b2c-setup.md`](docs/azure-entra-b2c-setup.md) for where that
  complexity actually shows up once you point this at a real Microsoft tenant instead
  of the toy `ExternalIdp`.
- ~~**`ExternalUserStore` is a `ConcurrentDictionary` that resets on every restart.**~~
  Resolved in Phase 5, below — `ExternalUserStore` is SQL-backed now.

## Phase 5 — persistence

Every store so far has been in-memory: `Config.cs`'s Clients/Resources via
`.AddInMemory*()`, and `ExternalUserStore`'s `ConcurrentDictionary`. Both reset on every
restart — a real IdG has been running continuously in production for years without
losing a single registered client. Phase 5 replaces both with SQL Server (LocalDB
locally), the same shape the real IdG actually uses.

### Duende's own stock EF stores — no custom `IClientStore`/`IResourceStore`

```csharp
var migrationsAssembly = typeof(Program).Assembly.GetName().Name;
var connectionString = builder.Configuration.GetConnectionString("IdentityServer");

.AddConfigurationStore(options =>
    options.ConfigureDbContext = b => b.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly)))
.AddOperationalStore(options =>
    options.ConfigureDbContext = b => b.UseSqlServer(connectionString, sql => sql.MigrationsAssembly(migrationsAssembly)))
```

This replaces `.AddInMemoryIdentityResources/ApiScopes/ApiResources/Clients` outright —
`Config.cs`'s lists are seed data now (see below), not the store itself.
`AddConfigurationStore` persists Clients/Resources/Scopes; `AddOperationalStore` persists
grants (refresh tokens, authorization codes, device codes, consent) that previously
vanished on restart along with everything else.

**Where this matches the real IdG exactly:** it has no custom `IClientStore` or
`IResourceStore` either — both use Duende's stock EF-backed stores as-is. The real
system's `CustomConfigurationDbContext`/`CustomPersistedGrantDbContext` subclasses exist
only to support a legacy DACPAC-migration-assembly quirk from an old IdentityServer v3
database — not something this sample needed to reproduce.

### `Data/SeedData.cs` — standing in for the real, deleted ingestion tool

The real IdG's Clients/Resources were never seeded in code at all — an external **Data
Ingestion Tool** wrote them directly into the same standard Duende tables this sample now
uses, and that tool has since been deleted from that codebase (the concept lives on as
this course's own Phase 6). `SeedData.EnsureSeedData`, called once before `app.Run()`, is
this sample's stand-in: it migrates all three `DbContext`s, then inserts `Config.cs`'s
lists into `ConfigurationDbContext` only if it's currently empty — idempotent, safe on
every restart, and simpler than the real system's `ApplyMigrations`-flag +
`DatabaseMigrationStartupTask` gate (fine for a single local database; that flag exists
in the real system to control *when* a shared, multi-instance production database gets
migrated, a problem this sample doesn't have).

### `Data/UserDbContext.cs` — the real system's other DbContext

The real IdG's persistence isn't *only* Duende's stores — it also has its own,
completely separate `UserDbContext`/`UserStore` for local user records, independent of
Duende entirely. `ExternalUserStore`'s new backing store mirrors that split:
`UserDbContext` owns an `ExternalUser`/`ExternalUserClaim` table pair, and
`ExternalUserStore` itself kept the exact same public shape it always had
(`ProvisionAsync`/`FindAsync`, both `async`) — `ExternalController` and
`SampleProfileService` didn't change at all. The one real change: `ExternalUserStore`
moved from `AddSingleton<>()` to the default scoped lifetime, because the reason it
needed to outlive a single request (provisioning has to survive past the request that
did it, for the token-issuance request that follows) is exactly what the database now
does instead.

### Verification

`pwsh ./test-phase5.ps1` re-runs the Phase 2–4 HTTP flows against the DB-backed server
(nothing regressed), then queries LocalDB directly via `sqlcmd` to prove the seed and
Carol's federated-login provisioning are real rows, not memory. The one thing a script
can't prove — that state survives an actual process restart, the whole point of this
phase — needs a manual stop/start of `IdentityServerHost`; see the script's own output
for the exact steps.

### What's deliberately still missing

- **No custom `IdentityProviderStore`.** `ExternalProviders` is still
  `appsettings.json`-only — the real IdG persists this in SQL too. Planned for a future
  phase (see the roadmap).
- ~~**`AddDeveloperSigningCredential()` is still a throwaway key on disk.**~~ Resolved
  in Phase 8 — `KeyManagement:Provider: "AzureKeyVault"` swaps in a real Key Vault-backed
  key. Still the default here, on purpose.
- **Migrations run automatically on every startup**, no gate. Fine for one local
  database; the real system's `ApplyMigrations` flag exists for a reason this sample
  doesn't have yet (a shared, multi-instance production database).

## Phase 6 — data ingestion / config tooling

Phase 5 made this app SQL-backed, but `Data/SeedData.cs` still seeded rows straight from
`Config.cs` — a C# file, compiled into the app, editable only by changing code and
redeploying. The real IdG doesn't work that way: config is data, edited and shipped
independently of the app that reads it. Phase 6 makes that true here too.

### `Configurations/IdentityServerConfig.json` — config is data now, not code

`Config.cs` is gone. In its place,
[`Configurations/IdentityServerConfig.json`](Configurations/IdentityServerConfig.json)
holds the exact same four lists — `identityResources`, `apiScopes`, `apiResources`,
`clients` — as plain JSON:

```json
{
  "clients": [
    {
      "clientId": "mvcclient",
      "clientSecret": "secret",
      "allowedGrantTypes": [ "authorization_code" ],
      "requirePkce": true,
      "requireConsent": false,
      "redirectUris": [ "https://localhost:5006/signin-oidc" ],
      "allowedScopes": [ "openid", "profile", "api1", "tenant" ]
    }
  ]
}
```

Grant-type strings (`"authorization_code"`, `"client_credentials"`) are the literal
protocol values Duende's own `GrantTypes.Code`/`GrantTypes.ClientCredentials` helpers
produce — the JSON doesn't need its own vocabulary for this, just the wire values.
Secrets are plaintext in the file (`"secret"`, not a hash) and get hashed at ingestion
time, the same moment `Config.cs`'s `new Secret("secret".Sha256())` used to — a real
deployment would pull the plaintext from a vault at that same moment, not commit it.

### `../Tools/ConfigIngestionTool` — a real, standalone ingestion tool

[`../Tools/ConfigIngestionTool`](../Tools/ConfigIngestionTool) reads that JSON file and
writes it into `ConfigurationDbContext` — the exact same database and tables
IdentityServerHost's `AddConfigurationStore()` reads from (Phase 5). It's its own console
project, run separately from IdentityServerHost itself:

```bash
cd src/Tools/ConfigIngestionTool
dotnet run
```

**Where this matches the real IdG:** the real system's config was never seeded in code
either — an external **Data Ingestion Tool**
(`src/Tools/IdentityGatewayConfigurationExporter` in that repo) wrote Clients/Resources
directly into the same standard Duende tables this sample uses. That tool has since been
deleted from the real codebase (only empty `bin`/`obj` folders remain), so its actual
input format and update strategy are lost — `ConfigIngestionTool`'s JSON shape and
ingestion logic are this course's own design, not a faithful reproduction of code nobody
can read anymore.

**Where this sample simplifies — the update strategy:** a key already in the database
(matched by `ClientId`, or `Name` for the three resource types) gets deleted and
reinserted from the JSON outright, not patched field-by-field. A key missing from the
JSON is left alone — this tool doesn't delete rows the file doesn't mention. A stricter
"full sync" would also prune those; this course picked the safer, more conservative
default on purpose (accidentally deleting a manually-added row is worse than leaving a
stale one behind), and doesn't know whether the real, deleted tool worked the same way.

**Because IdentityServerHost no longer seeds anything** (`Data/SeedData.cs` now only
calls `Database.Migrate()` on all three contexts, see Phase 5's section above for why it
used to seed), a freshly-migrated database has zero rows in `Clients` until someone runs
this tool. That's expected, not a bug — the same two-step "apply schema, then ingest
config" a real deployment actually goes through, now visible as two separate commands
instead of one `dotnet run` doing both.

### Two things that broke while building this

1. **A console app's "current directory" is not its build output directory.** The tool's
   first version resolved its `appsettings.json` and the JSON config path against
   `AppContext.BaseDirectory` (`bin/Debug/net10.0/`) — `dotnet run` failed immediately,
   looking for the config file several directories short of where it actually lives.
   Every other project in this course is run as `cd src/X && dotnet run`, so this tool
   resolves both paths against `Directory.GetCurrentDirectory()` instead, matching that
   same convention — and matching how `appsettings.json` loading works for every
   ASP.NET Core project in this repo already, which is *why* this wasn't caught earlier:
   it's the default for a `WebApplication`, just not for a bare console app, which has to
   opt in explicitly.
2. **`ConfigurationDbContext` needs a `ConfigurationStoreOptions` it can't get on its
   own.** Constructing it directly (`new ConfigurationDbContext(dbContextOptions)`)
   throws inside `OnModelCreating` — it resolves `ConfigurationStoreOptions` from its own
   internal service provider, which `AddConfigurationStore()` populates for free inside
   an ASP.NET Core host, but nothing populates for a plain console app building the
   context by hand. Fixed by constructing a minimal `ServiceCollection` with that one
   type registered as a singleton alongside `AddDbContext<ConfigurationDbContext>()`,
   the smallest container that satisfies what `OnModelCreating` actually asks for.

### Verification

`pwsh ./test-phase6.ps1` corrupts `mvcclient`'s `RequireConsent` flag directly in SQL
Server (simulating drift), re-runs `ConfigIngestionTool`, confirms the row is back to
matching the JSON, and re-runs `test-phase2.ps1`'s full login flow to prove the restored
client actually works — not just that the column looks right.

### What's deliberately still missing

- **No "full sync" / prune option.** A row the JSON file doesn't mention is never
  deleted, only ever left alone or replaced. See "Where this sample simplifies" above.
- **No dry-run mode.** The tool always writes; there's no way to preview a diff before
  committing it, something a real config-management tool would likely have.
- **One JSON file for everything.** The real IdG's actual ingestion format (environment
  overlays, per-tenant files, whatever it actually was) is unknown — lost with the
  deleted tool.

## Phase 7 — DIT external-service calls

Phase 3's `Tenants.cs` has always been a hardcoded dictionary standing in for something
the real IdG actually does over HTTP: it calls a sibling DIT microservice to resolve a
tenant key to a real database GUID. The real system's own docs already named this exact
gap (`IdentityServerHost/README.md`'s Phase 3 section: *"the actual tenant GUID lookup
(`TenantClient.GetTenantAsync`) is cached with `AbsoluteExpiration = DateTimeOffset.MaxValue`
— it never expires... `Tenants.cs` here is a hard-coded dictionary with no cache at all,
so the bug can't reproduce in this sample"*). Phase 7 ports the client, and — on
purpose — the bug.

### `../ExternalServicesStub` — a stand-in for two real DIT microservices

The real `TenantClient`/`UserClient` call a Tenant Management API and a User API —
independent DIT microservices this course doesn't have. `../ExternalServicesStub`
collapses both into one small process for the sake of this course, exposing the same two
routes the real clients actually call: `GET /v1/tenants/GetByKey/{key}` and
`GET /v2/User/identities/role/{subjectId}`, each backed by a hardcoded dictionary.

### `ExternalServices/TenantClient.cs` / `UserClient.cs` — self-issued JWTs, no secret

```csharp
var jwt = await tools.IssueClientJwtAsync(
    tenantOptions.JwtAuthentication.ClientId,
    lifetime: 300,
    ct,
    audiences: [tenantOptions.JwtAuthentication.Audience]);

var request = new HttpRequestMessage(HttpMethod.Get, $"{tenantOptions.Address}/v1/tenants/GetByKey/{tenantKey}");
request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
```

Every other client-to-IdentityServerHost call in this sample goes through
`/connect/token` with a registered `Client` and a secret. This one is different in kind:
`Duende.IdentityServer.IIdentityServerTools.IssueClientJwtAsync` mints a JWT **signed
with IdentityServerHost's own key** directly, with no OAuth round trip and no registered
client at all — `IdentityServerHost/Configurations/IdentityServerConfig.json` has no
entry for `"identityserverhost"` (the `ClientId` in the JWT's `client_id` claim) because
none is needed. `ExternalServicesStub` trusts the token for the same reason SampleApi
trusts every other access token in this sample: it's signed by, and validated against,
the same IdentityServerHost.

**Where this matches the real IdG:** this is the *exact* pattern the real `TenantClient`/
`UserClient` use — `IIdentityServerTools.IssueClientJwtAsync`, no secret, IdentityServer
acting as its own OAuth client against its sibling services.

### `SampleProfileService.cs` — one integration point instead of a custom `ITokenResponseGenerator`

The real IdG calls both clients from a custom `EquisoftTokenResponseGenerator`
(`ITokenResponseGenerator` override) at token-issuance time. This sample already had a
component that assembles claims at token-issuance time — `SampleProfileService`, built
in Phase 4 — so that's where both calls went instead of adding a second, parallel
component for the same job:

```csharp
var tenantKey = enrichedClaims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
if (tenantKey is not null)
{
    enrichedClaims.Add(new Claim("tenant_guid", await GetCachedTenantGuidAsync(tenantKey, ct)));
}

enrichedClaims.Add(new Claim("role", await userClient.GetRoleAsync(subjectId, ct)));
```

**Where this sample simplifies — additive, not a replacement:** the real IdG's
`tenant_id` claim *is* this GUID. This sample keeps its existing `tenant_id` claim as the
friendly key it's always been since Phase 3 and adds `tenant_guid` alongside it, rather
than changing what `tenant_id` contains — MvcClient's `ITenantContext` and SampleApi's
`IIdentityContext` both already resolve tenant *from* that key (see `CONTEXT.md`'s
`TenantClient`/`UserClient` entry), and changing its shape would ripple into both for a
phase that's only about proving this HTTP-call pattern out.

### The never-expiring cache bug — reproduced on purpose

```csharp
await cache.SetStringAsync(cacheKey, tenantGuid, new DistributedCacheEntryOptions
{
    AbsoluteExpiration = DateTimeOffset.MaxValue
}, ct);
```

`GetCachedTenantGuidAsync` wraps `TenantClient.GetTenantAsync` in an `IDistributedCache`
lookup (in-memory here; Redis in the real system — same interface, same bug either way,
since the bug is in the cache-entry *options*, not the backing store) keyed on
`tenant_id_from_key_{tenantKey}`, with the exact same never-expiring
`AbsoluteExpiration` the real system's own `EquisoftTokenResponseGenerator` uses.
Verified for real (not just asserted): changing `ExternalServicesStub`'s GUID for
`acme` and restarting *only* that project — not IdentityServerHost — still returned the
old, cached GUID on the next login. `UserClient.GetRoleAsync` has no cache at all
around it — the deliberate contrast sitting right next to it in the same method.

### Verification

`pwsh ./test-phase7.ps1` logs in as two different users/tenants, confirms `tenant_guid`
and `role` resolve correctly from `ExternalServicesStub` and reach both
IdentityServerHost's own token and SampleApi's independently-validated copy of it. The
cache bug itself isn't scripted — reproducing it needs editing `ExternalServicesStub`'s
code, not just data — see the script's own "try it yourself" output for the exact steps
(the same steps used to verify the claim above while writing this section).

### What's deliberately still missing

- **Only one of two real clients' full behavior.** The real `UserClient` short-circuits
  to a fixed `"Guest"` role without calling out at all for guest users — this sample has
  no guest concept, so every subject always calls out.
- **No resilience testing.** The Polly retry/circuit-breaker policies (reused verbatim
  from MvcClient's own) are wired up but never exercised by anything in this course —
  `ExternalServicesStub` never fails on purpose.
- **`Tenants.cs` (Phase 3) is unchanged.** It still resolves *which* tenant a login is
  for from `acr_values`; `TenantClient` only resolves that tenant's *GUID*, a
  downstream, additive step. The two were never the same concern, even though Phase 3's
  README caveat could be read that way at a glance.

## Phase 8 — signing-key management

Every token this sample has ever issued was signed with
`AddDeveloperSigningCredential()` — a throwaway RSA key written to `tempkey.jwk`
(gitignored) and reused across restarts, but never rotated, never access-controlled,
and gone the moment someone deletes that file. Phase 1's README named the real IdG's
actual answer to this from day one: `AddCertificates()`, loading a real certificate
from Azure Key Vault. Phase 8 ports it.

### `KeyManagement/SigningKeyExtensions.cs` — a dispatcher, not a store

```csharp
.AddSigningKey(builder.Configuration)
```

replaces `.AddDeveloperSigningCredential()` directly in `Program.cs`. Reading
`KeyManagement:Provider`, it either calls `AddDeveloperSigningCredential()` itself
(`Provider` unset or `"Developer"` — the default, so this sample keeps running with zero
Azure setup unless you opt in) or registers `AzureKeyVaultKeyStore` for
`Provider: "AzureKeyVault"`. **Where this sample simplifies:** the real
`AddCertificates()` branches across three providers (`None`/`Azure`/`Local`, the last one
loading a certificate from a local file path for on-premise deployments) — this course
only needed the two ends of that spectrum, so `Local` isn't ported.

### `KeyManagement/AzureKeyVaultKeyStore.cs` — one class, two Duende interfaces

```csharp
public class AzureKeyVaultKeyStore : ISigningCredentialStore, IValidationKeysStore
```

Exactly the real IdG's own shape (`IdentityServer/Stores/AzureKeyVaultKeyStore.cs`) — a
single class answering both "what do I sign with" and "what's currently valid to
verify with," backed by `Azure.Security.KeyVault.Certificates`' `CertificateClient`.
`SigningKeyExtensions` registers **one shared instance** for both interfaces
(`AddSingleton<AzureKeyVaultKeyStore>()`, then two `AddSingleton<TInterface>(sp =>
sp.GetRequiredService<AzureKeyVaultKeyStore>())` lines) rather than two independent
ones — otherwise there'd be two separate `CertificateClient`s and two separate cache
entries for what should be one fact.

### Rollover — every version becomes a validation key; only one signs

```csharp
var rolloverCutoff = DateTimeOffset.UtcNow.AddHours(-_options.RolloverDelayHours);
var signingVersion = candidates
    .Where(c => c.NotBefore <= rolloverCutoff)
    .OrderByDescending(c => c.NotBefore)
    .FirstOrDefault() ?? candidates.OrderByDescending(c => c.NotBefore).First();
```

A new certificate version doesn't immediately start signing tokens — it has to be older
than `RolloverDelayHours` (48 by default, same as the real system) first. Every
enabled, non-expired version — including brand-new ones still waiting out that delay —
becomes a **validation** key regardless, published via `jwks`. That ordering is the
entire point: a relying party's cached JWKS response has time to pick up a new key as
*valid* before this store ever picks it to actually *sign* with, and a token signed
moments before a rotation keeps validating because its signing version never stopped
being a validation key too.

**A real .NET gotcha, sidestepped rather than worked around:**
`X509Certificate2.NotBefore`/`NotAfter` are `DateTime` in the **local time zone**, not
UTC — a well-known trap for exactly this kind of comparison. This store compares
`Azure.Security.KeyVault.Certificates.CertificateProperties.NotBefore`/`ExpiresOn`
instead, which the Key Vault SDK returns as `DateTimeOffset`, always UTC — the
downloaded `X509Certificate2`'s own (local-time) fields are never compared against
anything here.

### Verified without a real vault: the dispatcher actually dispatches

Without Azure access in this environment, the actual Key Vault round trip couldn't be
tested end to end here — but the wiring was: pointing `KeyManagement:AzureKeyVault:VaultName`
at a name that cannot exist and hitting `/.well-known/openid-configuration/jwks`
produced a real `Azure.RequestFailedException` — DNS resolution failing against
`nonexistent-vault-xyz123.vault.azure.net`, four retries deep through the Azure SDK's own
retry policy, with `AzureKeyVaultKeyStore.LoadKeysFromVaultAsync` in the stack trace.
That's proof the dispatcher genuinely activates the Key Vault path and makes a real
network attempt — not a silently-successful fallback to the developer key. See
[`docs/azure-key-vault-setup.md`](docs/azure-key-vault-setup.md) for how to create a
real vault and verify an actual successful round trip, including certificate rotation.

### Verification

`pwsh ./test-phase8.ps1` confirms the default developer-key path still signs tokens
normally after adding the Key Vault code path, then prints the manual steps above (the
same ones used to verify the claim while writing this section) for proving the
`AzureKeyVault` path is really wired up — restarting a service mid-script with different
config isn't something any other `test-phaseN.ps1` does either.

### What's deliberately still missing

- **No auto-renewal.** This store reads whatever certificate versions already exist; it
  never asks Key Vault to issue a new one. A real deployment would pair this with Key
  Vault's own certificate lifecycle actions (auto-renew before expiry) — out of scope
  for what this phase is teaching.
- **No `Local` provider.** See "where this sample simplifies" above.
- **No resilience policy around the Key Vault calls**, matching the real system exactly
  — it has none either, relying on the Azure SDK's own built-in retry behavior (visible
  in the "four retries" trace above).

## Running it

0. **Prerequisites.**
   - **(Phase 5+) SQL Server LocalDB.** `IdentityServerHost` needs a
     `(localdb)\mssqllocaldb` instance reachable at startup — it ships with Visual
     Studio, or install it standalone via the SQL Server Express LocalDB installer.
     `dotnet run` creates the `MiniIdG` database and applies migrations on its own; it no
     longer seeds any rows (Phase 6).
   - **(Phase 6+) Run the data-ingestion tool once** — `cd src/Tools/ConfigIngestionTool
     && dotnet run` — after `IdentityServerHost` has run at least once (to create the
     database/schema) and before logging in anywhere (there are no clients until this
     runs). Safe to re-run any time
     [`Configurations/IdentityServerConfig.json`](Configurations/IdentityServerConfig.json)
     changes.

1. **Six terminals** — every project's `launchSettings.json` already pins its own port
   (`https://localhost:5001` for IdentityServerHost, `5011` ExternalIdp, `5006`
   MvcClient, `5007` SampleApi, `5012` ExternalServicesStub, `5173` ReactSpa), so a plain
   `dotnet run` in each is enough:

   ```bash
   # terminal 1
   cd src/ExternalIdp
   dotnet run

   # terminal 2
   cd src/ExternalServicesStub
   dotnet run

   # terminal 3
   cd src/IdentityServerHost
   dotnet run

   # terminal 4
   cd src/MvcClient
   dotnet run

   # terminal 5
   cd src/SampleApi
   dotnet run

   # terminal 6
   cd src/ReactSpa
   npm install   # first time only
   npm run dev
   ```

2. **MVC flow** — browse to `https://localhost:5006`, click *Go to the secure page*, and
   sign in as `alice` / `alice` (or `bob` / `bob`). You'll land back on the secure page
   with a table of every claim in your identity — `sub`, `name`, `idp`, and the token
   timestamps. Notably *not* `email`, even though `alice`'s `TestUser` has one and
   `profile` is in scope: per the OIDC spec, `profile` and `email` are two separate
   standard scopes, and `new IdentityResources.Profile()` genuinely doesn't carry
   `email` in its `UserClaims`. Adding `new IdentityResources.Email()` to `Config.cs`
   (and `"email"` to a client's requested scopes) is exactly the kind of thing Phase 1's
   own "try it yourself" suggested experimenting with.

3. **Call the API** — from the secure page, click *Call the API*. MvcClient forwards
   its access token to SampleApi as a `Bearer` header; SampleApi validates it
   independently and echoes back every claim it found — including `aud: api1` and
   `scope: api1`, proving the audience/scope checks actually ran, not just "some token
   was present."

4. **React SPA flow** — browse to `http://localhost:5173`, click **Log in**, sign in as
   `alice` / `alice`. You land back on the SPA (not redirected to a different app's
   page) with the same kind of claims table — this time decoded entirely client-side
   from a token that never touched a server. Click *Call the API* there too — this time
   the browser's own `fetch()` calls SampleApi directly across origins (`:5173` →
   `:5003`), which is why SampleApi now has a CORS policy (see its README).

5. **Tenant resolution, in a browser** — from `https://localhost:5006`, click *Log in as
   Acme Corp* or *Log in as Globex Corporation* (see MvcClient's README for how these
   set `acr_values` before redirecting). Try `alice`/`alice` on Acme (succeeds,
   `tenant_id: acme` on the claims table) and on Globex (rejected — right password,
   wrong tenant). `reactspa` doesn't send `acr_values` yet (still an open exercise —
   see its README), so the same trick there still requires constructing the authorize
   URL by hand, the way [`test-phase3.ps1`](../../test-phase3.ps1) does over raw HTTP:
   Alice into Acme (succeeds, correct `tenant_id` claim), Alice into Globex (rejected),
   Bob into Globex (succeeds), and a login with no tenant hint at all (still works —
   Phase 2 is unaffected).

6. **External IdP federation** — from the same *Log in as Acme Corp* link, click
   **Sign in with ExternalIdp** instead of using the local form, and sign in as
   `carol`/`carol` (a user that only exists on the separate ExternalIdp server on port
   5011) — you'll land back on MvcClient's secure page with `name: Carol Chen` and
   `tenant_id: acme`, even though ExternalIdp itself never heard the word "acme."
   Globex's login page has no such button at all — try *Log in as Globex Corporation*
   to confirm. [`test-phase4.ps1`](../../test-phase4.ps1) drives the same round trip
   over raw HTTP. Want a real Microsoft tenant instead of the toy `ExternalIdp`? See
   [`docs/azure-entra-b2c-setup.md`](docs/azure-entra-b2c-setup.md).

7. **Prefer not to click through a browser?**
   [`test-phase2.ps1`](../../test-phase2.ps1) drives the MVC login flow over raw HTTP;
   [`test-api.ps1`](../../test-api.ps1) does the same login and then drives *Call the
   API*; [`test-phase2-spa.ps1`](../../test-phase2-spa.ps1) proves the React SPA's
   IdentityServer-side login configuration (public client, no secret, CORS on
   `/connect/token`); [`test-spa-api.ps1`](../../test-spa-api.ps1) proves the same for
   its *Call the API* button (the `api1` scope, and SampleApi's own CORS policy);
   [`test-phase3.ps1`](../../test-phase3.ps1) is the tenant-resolution scenarios from
   step 5; [`test-phase4.ps1`](../../test-phase4.ps1) is the federated-login scenario
   from step 6; [`test-phase5.ps1`](../../test-phase5.ps1) re-runs phases 2–4 against the
   now DB-backed server and queries LocalDB directly to confirm the seed/provisioning
   landed in SQL Server; [`test-phase6.ps1`](../../test-phase6.ps1) corrupts a client
   directly in the database and confirms `ConfigIngestionTool` restores it;
   [`test-phase7.ps1`](../../test-phase7.ps1) confirms `tenant_guid`/`role` resolve from
   `ExternalServicesStub` and reach both IdentityServerHost's and SampleApi's tokens;
   [`test-phase8.ps1`](../../test-phase8.ps1) confirms the default developer signing key
   still works, then prints manual steps for proving the Key Vault path (see
   [`docs/azure-key-vault-setup.md`](docs/azure-key-vault-setup.md)). None of the ten
   drive real browser JavaScript, so steps 2–4 above still need a manual pass at least
   once (see `ReactSpa`'s README for why):

   ```powershell
   pwsh ./test-phase2.ps1
   pwsh ./test-api.ps1
   pwsh ./test-phase2-spa.ps1
   pwsh ./test-spa-api.ps1
   pwsh ./test-phase3.ps1
   pwsh ./test-phase4.ps1
   pwsh ./test-phase5.ps1
   pwsh ./test-phase6.ps1
   pwsh ./test-phase7.ps1
   pwsh ./test-phase8.ps1
   ```

## What's deliberately missing (and why)

- **Real business data or logic behind the API.** SampleApi has exactly one endpoint
  that echoes claims — it exists to prove token validation works, not to be a real
  service. Also see its own README for its list of "deliberately missing."
- **Scope-level authorization beyond one policy.** The only check SampleApi makes is
  "does this token carry the `api1` scope." Finer-grained authorization (roles,
  per-tenant policies) isn't needed yet with only two clients and one API.
- **`LinkedTenants` or any tenant-linking concept.** A user either matches the requested
  tenant or is rejected outright — no escape hatch for a user who legitimately belongs
  to more than one tenant.
- **ReactSpa setting `acr_values`.** MvcClient now does (see its README's "Logging in as
  a specific tenant" section — including two real gotchas that took actually clicking
  the button to find: Pushed Authorization Requests hiding the parameter entirely, and a
  missing `ClaimAction` silently dropping `tenant_id` from the merged claims). Wiring the
  same into ReactSpa (`oidc-client-ts`'s `signinRedirect({ acr_values: ... })`) is still
  open — see its README's "try it yourself" section.
- ~~**Persistence.**~~ Resolved in Phase 5 — clients, resources, grants, and provisioned
  external identities are all SQL Server-backed now. `TestUsers.cs` (test-user
  credentials) is still hard-coded in-memory on purpose — the real IdG has no local
  password login at all.
- ~~**Config baked into a compiled C# file.**~~ Resolved in Phase 6 — `Config.cs` is
  gone; `Configurations/IdentityServerConfig.json` plus
  `../Tools/ConfigIngestionTool` are the source of truth now, editable and re-ingested
  without a rebuild.
- **`Tenants.cs` is still a hard-coded dictionary, deliberately.** Phase 7 ported the
  *GUID-lookup* half of the real system's `TenantClient` (see its own section above),
  not tenant resolution itself — `Tenants.ResolveTenantKey` (*which* tenant a login is
  for, parsed from `acr_values`) is a different concern from `TenantClient.GetTenantAsync`
  (*that* tenant's GUID), and only the second one is a real HTTP call in either system.
- ~~**A throwaway dev signing key, with no production equivalent.**~~ Resolved in
  Phase 8 — `KeyManagement:Provider: "AzureKeyVault"` swaps in a real Key Vault-backed
  key, verified to genuinely activate (see its own section above), though not verified
  against a real vault in this environment — see
  [`docs/azure-key-vault-setup.md`](docs/azure-key-vault-setup.md) for that. Still
  defaults to the developer key, on purpose, so this sample runs with zero Azure setup
  unless you opt in.
- **Claim-mapping complexity for external logins.** No `FederatedConfiguration`, no
  configurable external-id claim name, no duplicate-claim handling — Carol's `name`
  claim just works because ExternalIdp only ever sends one of it.
- **A license key.** Still fine for local dev forever; still out of scope for this
  learning project.

## Try it yourself before moving on

Remove `RequirePkce = true` from `mvcclient` and re-run `test-phase2.ps1` — does
anything visibly break? (It won't, for this sample — PKCE closes an interception attack
that requires a man-in-the-middle to exploit, not something a working solo flow will
ever surface.)

Change `RequireClientSecret` back to its default (`true`) on `reactspa` and re-run
`test-phase2-spa.ps1` — read the error the token endpoint gives you back this time.

Comment out `app.UseCors("ReactSpa")` in `SampleApi/Program.cs`, restart SampleApi, and
click *Call the API* in a real browser at `http://localhost:5173` — `test-spa-api.ps1`
would still pass (raw `HttpClient` doesn't enforce CORS the way a browser does), but the
browser itself will refuse the request. Read what a CORS failure actually looks like in
dev tools' console — it's a distinct failure mode from a `401`, worth being able to
recognize on sight.

Remove `"name"` from the `ApiResource`'s `UserClaims` and re-run *Call the API*. The
`email` claim still shows up, `name` doesn't — confirming that each claim riding on an
access token was put there deliberately, one at a time, not "whatever the user has."

MvcClient's *Log in as Acme Corp* / *Log in as Globex Corporation* links already let you
click through a real tenant mismatch in a browser — try `bob`/`bob` on Acme, or
`alice`/`alice` on Globex. Then ask yourself: *"What would `LinkedTenants` actually let
two tenants share?"* — that's the escape hatch this sample didn't implement.

Try adding a **second** `OpenId` entry to `appsettings.Development.json`, `EcosystemTenant: "acme"`,
pointing at the same `ExternalIdp` under a different `Name` (you'll need a second client
registration in `ExternalIdp/Config.cs` to go with it) — no `Program.cs` change needed
this time. What does the login page do differently now? Then ask: *"How would this look
with a real Entra tenant instead of ExternalIdp?"* — see
[`docs/azure-entra-b2c-setup.md`](docs/azure-entra-b2c-setup.md) for exactly that.
