# IdentityServerHost

This is the authorization server half of a mini "Identity Gateway" built from scratch,
one phase at a time, so that each phase is a small, runnable slice of what a real
OAuth/OIDC authorization server (like Equisoft's `Applications.IdentityGateway`)
actually is under all its production scaffolding.

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ✓
4. External identity providers ← next
5. Persistence (SQL Server instead of in-memory)
6. Data ingestion / config tooling
```

The sibling projects [`../MvcClient`](../MvcClient) and [`../ReactSpa`](../ReactSpa) are
the other half of Phase 2 — two applications, of the two kinds the target architecture
actually has (a server-side app and a browser-only SPA), that log a user in against this
server. Their own READMEs cover what each is; this one covers the IdentityServer side of
both flows.

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
Browser  ↔  MvcClient (:5002)  —code + PKCE→  IdentityServerHost (:5000)
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

    RedirectUris = { "http://localhost:5002/signin-oidc" },
    PostLogoutRedirectUris = { "http://localhost:5002/signout-callback-oidc" },

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
        UserClaims = { "name", "email" }
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
- **`UserClaims = { "name", "email" }`** — by default an access token carries only
  protocol claims (`sub`, `scope`, `client_id`, ...), *not* the identity claims that
  ended up in the ID token via the `profile` scope. An access token and an ID token
  don't automatically share claims; this list is what copies `name`/`email` onto the
  access token too, so SampleApi has something more interesting than `sub` to show.

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

> **Known real-system caveat, worth carrying forward:** the actual tenant GUID lookup
> (`TenantClient.GetTenantAsync`) is cached with `AbsoluteExpiration =
> DateTimeOffset.MaxValue` — it never expires. A tenant's GUID changing in the source of
> truth would not be picked up without an app restart. `Tenants.cs` here is a
> hard-coded dictionary with no cache at all, so the bug can't reproduce in this sample
> — mentioned so the absence doesn't read as "this sample proves it's fine."

### `Config.cs` — an opt-in `tenant` scope

```csharp
new IdentityResource { Name = "tenant", DisplayName = "Tenant", UserClaims = { "tenant_id" } }
```

Added to both `mvcclient`'s and `reactspa`'s `AllowedScopes`. Same pattern as `api1` in
the SampleApi section above: this is an allowlist, not a request — a client still has
to put `"tenant"` in its own `Scope`/`scope` list for `tenant_id` to actually show up
anywhere.

## Running it

1. **Four terminals**

   ```bash
   # terminal 1
   cd src/IdentityServerHost
   dotnet run

   # terminal 2
   cd src/MvcClient
   dotnet run --urls http://localhost:5002

   # terminal 3
   cd src/SampleApi
   dotnet run --urls http://localhost:5003

   # terminal 4
   cd src/ReactSpa
   npm install   # first time only
   npm run dev
   ```

2. **MVC flow** — browse to `http://localhost:5002`, click *Go to the secure page*, and
   sign in as `alice` / `alice` (or `bob` / `bob`). You'll land back on the secure page
   with a table of every claim in your identity — `sub`, `name`, `email`, `idp`, and the
   token timestamps.

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

5. **Tenant resolution** — [`test-phase3.ps1`](../../test-phase3.ps1) drives four
   scenarios end-to-end against `reactspa`: Alice into Acme (succeeds, correct
   `tenant_id` claim), Alice into Globex (rejected), Bob into Globex (succeeds), and a
   login with no tenant hint at all (still works — Phase 2 is unaffected). To see it in
   a browser, you'd need a client that sets `acr_values` on its challenge — neither
   MvcClient nor ReactSpa does that yet; wiring one up (Microsoft's OIDC handler exposes
   it via `OpenIdConnectChallengeProperties.AcrValues`) is this phase's practice
   exercise, not something already done for you.

6. **Prefer not to click through a browser?**
   [`test-phase2.ps1`](../../test-phase2.ps1) drives the MVC login flow over raw HTTP;
   [`test-api.ps1`](../../test-api.ps1) does the same login and then drives *Call the
   API*; [`test-phase2-spa.ps1`](../../test-phase2-spa.ps1) proves the React SPA's
   IdentityServer-side login configuration (public client, no secret, CORS on
   `/connect/token`); [`test-spa-api.ps1`](../../test-spa-api.ps1) proves the same for
   its *Call the API* button (the `api1` scope, and SampleApi's own CORS policy);
   [`test-phase3.ps1`](../../test-phase3.ps1) is the tenant-resolution scenarios from
   step 5. None of the five drive real browser JavaScript, so steps 2–4 above still
   need a manual pass at least once (see `ReactSpa`'s README for why):

   ```powershell
   pwsh ./test-phase2.ps1
   pwsh ./test-api.ps1
   pwsh ./test-phase2-spa.ps1
   pwsh ./test-spa-api.ps1
   pwsh ./test-phase3.ps1
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
- **A client that actually sets `acr_values`.** Neither MvcClient nor ReactSpa
  challenges with a tenant hint yet — `test-phase3.ps1` proves the IdentityServer side
  works by crafting that query parameter directly. Wiring a real client up to do this is
  the practice exercise for this phase.
- **Persistence.** Everything still resets to empty on every restart except
  `tempkey.jwk` (the signing key) — clients, resources, tenants, and test users are all
  in-memory C#. A later phase replaces the in-memory stores with a real database.
- **External IdPs.** No user in this sample logs in through anything but the local
  password form yet — that's Phase 4, next.
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

Wire `OpenIdConnectChallengeProperties.AcrValues = "tenant:acme"` into a new action on
MvcClient's `HomeController` (it's just an extra property on the object passed to
`Challenge(properties)`), then click through a real mismatch in a browser: hit that
action while trying to sign in as `bob`. Then ask yourself: *"What would `LinkedTenants`
actually let two tenants share?"* — that's the escape hatch this sample didn't
implement, and a reasonable question to have before Phase 4 adds a second way to prove
who someone is.
