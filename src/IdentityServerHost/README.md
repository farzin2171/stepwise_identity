# IdentityServerHost

This is the authorization server half of a mini "Identity Gateway" built from scratch,
one phase at a time, so that each phase is a small, runnable slice of what a real
OAuth/OIDC authorization server (like Equisoft's `Applications.IdentityGateway`)
actually is under all its production scaffolding.

```
1. Foundation ✓
2. Clients (MVC ← you are here, React next)
3. Multi-tenancy
4. External identity providers
5. Persistence (SQL Server instead of in-memory)
6. Data ingestion / config tooling
```

The sibling project [`../MvcClient`](../MvcClient) is the other half of Phase 2 — an
application that actually logs a user in against this server. Its own README covers
what it is; this one covers the IdentityServer side of the same flow.

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

### `Config.cs` — `mvcclient` now asks for `api1`

```csharp
AllowedScopes =
{
    IdentityServerConstants.StandardScopes.OpenId,
    IdentityServerConstants.StandardScopes.Profile,
    "api1"
}
```

`AllowedScopes` on the `Client` is an allowlist — it says what `mvcclient` is *permitted*
to request, not what it *does* request. MvcClient's own `Program.cs` has to separately
add `"api1"` to its `Scope` collection for a token to actually come back with it. Two
places, two different jobs: IdentityServerHost decides what's allowed; the client
decides what it asks for on any given login.

### `Program.cs` — one more in-memory store

```csharp
.AddInMemoryApiScopes(Config.ApiScopes)
.AddInMemoryApiResources(Config.ApiResources)
```

Same pattern as every other `.AddInMemory...()` call in this file — a real IdG would
call `.AddApiResources()`/`.AddApiScopes()` against the same SQL-backed configuration
store as everything else (Phase 5 territory), not a different mechanism.

## Running it

1. **Three terminals**

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
   ```

2. **Browse to `http://localhost:5002`**, click *Go to the secure page*, and sign in as
   `alice` / `alice` (or `bob` / `bob`). You'll land back on the secure page with a table
   of every claim in your identity — `sub`, `name`, `email`, `idp`, and the token
   timestamps.

3. **Click *Call the API***. MvcClient forwards its access token to SampleApi as a
   `Bearer` header; SampleApi validates it independently and echoes back every claim it
   found — including `aud: api1` and `scope: api1`, proving the audience/scope checks
   actually ran, not just "some token was present."

4. **Prefer not to click through a browser?**
   [`test-phase2.ps1`](../../test-phase2.ps1) (repo root) drives the login flow alone
   over raw HTTP; [`test-api.ps1`](../../test-api.ps1) does the same login and then
   drives the *Call the API* step too:

   ```powershell
   pwsh ./test-phase2.ps1
   pwsh ./test-api.ps1
   ```

## What's deliberately missing (and why)

- **Real business data or logic behind the API.** SampleApi has exactly one endpoint
  that echoes claims — it exists to prove token validation works, not to be a real
  service. Also see its own README for its list of "deliberately missing."
- **A second client, or scope-level authorization beyond one policy.** Every client that
  could ever call SampleApi is `mvcclient`, and the only check is "does this token carry
  the `api1` scope." Finer-grained authorization (roles, per-tenant policies) isn't
  needed yet with only one client and one API.
- **Persistence.** Everything still resets to empty on every restart except
  `tempkey.jwk` (the signing key) — clients, resources, and test users are all in-memory
  C#. A later phase replaces the in-memory stores with a real database.
- **Multi-tenancy and external IdPs.** Both `alice` and `bob` are plain local accounts
  with no tenant concept and no external identity provider behind them — that's Phases 3
  and 4.
- **A license key.** Still fine for local dev forever; still out of scope for this
  learning project.

## Try it yourself before moving on

Remove `RequirePkce = true` from the client and re-run the flow — does anything visibly
break? (It won't, for this sample — PKCE closes an interception attack that requires a
man-in-the-middle to exploit, not something a working solo flow will ever surface.) Then
ask yourself: *"What would a React SPA's client need to look like differently, and why
can't it use a `ClientSecret`?"* That's exactly where the next lesson (the React client,
still Phase 2) starts, before Phase 3 (multi-tenancy).

Separately: remove `"name"` from the `ApiResource`'s `UserClaims` and re-run *Call the
API*. The `email` claim still shows up, `name` doesn't — confirming that each claim
riding on an access token was put there deliberately, one at a time, not "whatever the
user has."
