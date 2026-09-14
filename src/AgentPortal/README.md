# AgentPortal — Phase 17: a second MVC client

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ✓
4. External identity providers ✓
5. Persistence (SQL Server instead of in-memory) ✓
6. Data ingestion / config tooling ✓
7. DIT external-service calls (TenantClient, UserClient) ✓
8. Signing-key management (Key Vault instead of a developer credential) ✓
9. IdentityProviderStore (DB-persisted external-provider config) ✓
10. Mini.Infrastructure (extract the genuinely duplicated plumbing) ✓
11. Mini.UserService (a real service replaces ExternalServicesStub) ✓
12. Connectors (per-tenant, cascading user sources) ✓
13. Mini.AuthorizationService (the permission decision leaves the token) ✓
14. (authorization integration — SampleApi calls the authorization service) ✓
15. (persist authorization decisions across restarts) ✓
16. Shared authorization client (extracted into Mini.Infrastructure, made resilient) ✓
17. Agent Portal skeleton (a second MVC client, imitating Apply) ✓
18. (Agent Portal calls Mini.AuthorizationService via the shared client) ← next
```

## Why this phase

Every project in this repo so far has answered a different question about **one** client
(`MvcClient`) talking to **one** authorization server (`IdentityServerHost`). Phase 17 asks a
different question: does a **second**, independently-configured client coexist on the same
server without stepping on the first one?

In production, `Applications.Apply` is not the only MVC/BFF-style client hitting the real
Identity Gateway — `Applications.Portal`, `Applications.AdminConsole`, and
`Applications.CustomerPortal` are separate applications, each with its own client
registration, each logging a user in against the same IdG. This project imitates that shape:
a plausible second Apply-like client, sharing nothing with `MvcClient` except the
authorization server and the `Mini.Infrastructure` project reference.

**Be honest about what "Agent Portal" is**: it does not port a real Equisoft application named
"Agent Portal." A repo-wide grep of `Applications.Apply` turned up no such concept, and no
sibling repo under `C:\work` is named that either. It is illustrative — a second, plausibly-named
MVC client — not a 1:1 port of a specific real system the way `MvcClient` ports `Applications.Apply`
itself or `Mini.UserService` ports `Services.User`. The real precedent this project stands in for
is "one more Equisoft app in the list of things that log into the IdG," not any one specific app.

Phase 17 is deliberately a **skeleton**: it proves login works with its own client registration
and nothing more. It does **not** call any downstream API, does not resolve a tenant, and does
not touch `Mini.AuthorizationService` — that's Phase 18, once this project has a reason to make
an authorized call at all.

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
           options.Authority = "https://localhost:5001";
           options.ClientId = "agentportal";
           options.ClientSecret = "agentportal-secret";
           options.ResponseType = "code";
           options.UsePkce = true;
           options.Scope.Clear();
           options.Scope.Add("openid");
           options.Scope.Add("profile");
           options.SaveTokens = true;
           options.GetClaimsFromUserInfoEndpoint = true;
           // ...
       });
```

Structurally identical to `MvcClient/Program.cs`'s authentication block — same two-scheme
shape (`cookies` to hold the session, `oidc` only to establish it), same
`SaveTokens`/`GetClaimsFromUserInfoEndpoint` reasons. The differences are the point:

- **`ClientId = "agentportal"`**, not `"mvcclient"` — its own row in
  `IdentityServerConfig.json`, ingested by the same `ConfigIngestionTool` MvcClient's client
  goes through.
- **`Scope` is just `openid profile`** — no `api1` (no downstream API call exists yet) and no
  `tenant` (no tenant resolution in this phase).
- **No `OnRedirectToIdentityProvider` event, no `acr_values`** — MvcClient's `LoginAsTenant()`
  action and its accompanying event hook exist to stamp a tenant hint onto the redirect before
  it happens; this project never sets one, so it never needs the hook.
- **`PushedAuthorizationBehavior` is left at the handler's default** (`UseIfAvailable`)
  instead of `Disable`. MvcClient disables PAR specifically because its
  `TenantResolutionMiddleware` does raw query-string parsing for `acr_values` (see its
  README's Phase 3 section) and PAR hides that parameter behind an opaque `request_uri`.
  Agent Portal never sends `acr_values` at all, so there's nothing PAR could hide from it —
  see "Things that broke" below for what this looked like in practice.

### `IdentityServerConfig.json` (new client entry)

```json
{
  "clientId": "agentportal",
  "clientSecret": "agentportal-secret",
  "allowedGrantTypes": [ "authorization_code" ],
  "requirePkce": true,
  "requireConsent": false,
  "redirectUris": [ "https://localhost:5016/signin-oidc" ],
  "postLogoutRedirectUris": [ "https://localhost:5016/signout-callback-oidc" ],
  "allowedScopes": [ "openid", "profile" ]
}
```

Same shape as `mvcclient`'s entry (confidential, `authorization_code` + PKCE), on its own
port (`5016`), with its own secret. `run-all.ps1` runs `ConfigIngestionTool` before starting
any host, so this row exists in the database before Agent Portal's first login attempt.

### `Controllers/HomeController.cs`

```csharp
public class HomeController : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    public IActionResult Secure() => View(User.Claims);
}
```

The whole controller. `Index` is public; `Secure` triggers the standard ASP.NET Core
challenge → redirect → login → callback flow, then prints every claim on the resulting
`ClaimsPrincipal` — the same proof-of-login pattern `MvcClient/Views/Home/Secure.cshtml` uses,
minus the tenant/API-call additions that came later in that project's own phase history.

## Comparison against the real counterparts

| This sample | Real counterpart | Notes |
| --- | --- | --- |
| `AgentPortal` (this project) | No single real counterpart | Illustrative: "a second Apply-like MVC client," not a port of one specific app. |
| `MvcClient`'s multi-tenancy/`ExternalServicesApi` port | `Applications.Apply`, `src/Equisoft.Apply/Infrastructure/Authentication/Extensions/IServiceCollectionExtensions.cs` (`ConfigureAuthentication`, called from `Startup.cs`) | AgentPortal deliberately does **not** yet port this — see "Where this sample simplifies." |
| Coexistence of multiple client apps on one IdG | `Applications.Portal`, `Applications.AdminConsole`, `Applications.CustomerPortal` alongside `Applications.Apply`, all under `C:\work`, all presumed clients of the same production Identity Gateway | This is the actual real-world shape Phase 17 is imitating — not any one of those apps specifically, since none of them is publicly named "Agent Portal." |

## Where this sample simplifies

| Real IdG / Apply | This sample (Phase 17) |
| --- | --- |
| A production client like Apply carries full multi-tenancy (`ITenantContext`, tenant-aware login redirects) from day one. | Agent Portal has none of that yet — no tenant scope, no `LoginAsTenant()`, no `RequireTenantAttribute`. It's a skeleton proving login works with its own client id; multi-tenancy is a feature to *add*, not one this phase ports. |
| A real client typically already calls at least one downstream API using its own access token or a service-account token (see `MvcClient`'s `CallApi`/`CallApiAsServiceAccount`). | Agent Portal makes no downstream call in this phase — there's nothing yet for it to be authorized to do. Phase 18 gives it exactly one: calling `Mini.AuthorizationService` through `Mini.Infrastructure`'s shared `AuthorizationClient` (see that project's Phase 16 section). |
| Apply disables PAR specifically to keep `acr_values` visible to its own tenant-resolution middleware. | Agent Portal has no tenant hint to protect, so it leaves `PushedAuthorizationBehavior` at the handler's default — a genuinely different, not merely smaller, decision. See "Things that broke." |
| A real client's secret and redirect URIs are provisioned through whatever secrets/config pipeline that team owns. | Both live in plaintext in `IdentityServerConfig.json`, same as every other client in this sample — a teaching simplification already established by Phase 2, not a new one. |

## Things that broke, and why they're worth knowing

**1. The PAR-shaped redirect URL, discovered by writing `test-phase17.ps1`.** The first version
of the test asserted the classic, fully-visible `?client_id=agentportal&...` query string on the
redirect to IdentityServerHost — the same shape MvcClient's own tests match against. It failed
immediately: the actual URL was
`https://localhost:5001/connect/authorize/callback?request_uri=urn:ietf:params:oauth:request_uri:...&client_id=agentportal`.
The reason is exactly the inverse of MvcClient's Phase 3 gotcha (see its README): MvcClient
explicitly sets `PushedAuthorizationBehavior.Disable` because its tenant-resolution middleware
needs `acr_values` visible on the query string. Agent Portal never disabled PAR — there was no
reason to, since it never sends `acr_values` — so Duende's OIDC handler took the discovery
document's advertised `pushed_authorization_request_endpoint` and used it, exactly as designed.
The fix wasn't a code change; it was correcting the test's assumption about which client shape
it was looking at. Worth knowing: **PAR-or-not is a per-client decision in this handler**, not a
server-wide setting — two clients against the very same IdentityServerHost can produce visibly
different authorize redirects depending only on their own OIDC handler configuration.

**2. The form-post callback, missed on the first pass at `test-phase17.ps1`.** Copying
`test-phase3.ps1`'s `Follow()` helper (built for the `reactspa` client, which gets a
`response_type=code&response_mode=query` redirect back to a literal URL) produced an
unhandled case: the real callback from `/connect/authorize/callback` back to Agent Portal is an
auto-submitting HTML form (`response_mode=form_post`), not a 3xx redirect — `Follow()` only
follows 3xx responses, so it returned the raw form HTML instead of completing the login. The fix
was to parse and manually POST the form's hidden fields to its `action` URL, the same second
step `test-api.ps1` (MvcClient's own API-call test) already needed for exactly this reason. This
wasn't new information for the repo — it confirms MvcClient's test wasn't special-cased, it's
just what a confidential client's authorize callback looks like here — but it cost a real,
reproducible test failure to rediscover on this project rather than reuse by inspection.

## What's deliberately missing (as of Phase 17)

- **Tenant resolution.** No `ITenantContext`, no `LoginAsTenant()`, no tenant scope requested.
  A real Apply-like client would have this from the start; here it's added only once there's a
  concrete reason to need it.
- **Any downstream API call.** No `HttpClient`, no `ITokenClient`, no `SampleApi` or
  `Mini.AuthorizationService` reference. Phase 18 adds exactly one: a call to
  `Mini.AuthorizationService` via `Mini.Infrastructure`'s shared `AuthorizationClient`.
- **A distinct home page beyond "prove login works."** No feature surface of its own yet — that
  would be premature before Phase 18 gives it something to be authorized *for*.

## Try it yourself

Start `IdentityServerHost` and `AgentPortal` (or `.\run-all.ps1`), then:

1. Browse to `https://localhost:5016` and click **Go to the secure page**, sign in as
   `alice`/`alice`. You'll land on Agent Portal's own claims table — a separate session,
   a separate client id, the same IdentityServerHost `MvcClient` uses.
2. With both `MvcClient` (`:5006`) and `AgentPortal` (`:5016`) running, sign into each in the
   **same** browser. Notice they don't share a session — each app's `cookies` scheme is scoped
   to its own origin, so signing out of one doesn't touch the other, even though both trust the
   same IdentityServerHost.
3. Break the PAR assumption on purpose: set
   `options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;` in
   `Program.cs`, restart, and watch `test-phase17.ps1`'s second assertion — the URL shape
   changes back to the classic query string, proving the "Things that broke" finding above
   really is about this project's own OIDC handler configuration, not something server-side.

Prefer not to click through a browser? [`test-phase17.ps1`](../../test-phase17.ps1) (repo root)
drives the whole login end-to-end over raw HTTP, in three parts: the public home page needs no
session, `/Home/Secure` challenges to IdentityServerHost as `agentportal` specifically (in its
PAR-shaped form), and a real `alice`/`alice` login completes the code+token exchange and reaches
Agent Portal's own secure page.

## Running it

```bash
# terminal 1
cd ../IdentityServerHost && dotnet run

# terminal 2
cd . && dotnet run --urls https://localhost:5016
```

Or just `.\run-all.ps1` from the repo root — it now starts `AgentPortal` alongside everything
else.
