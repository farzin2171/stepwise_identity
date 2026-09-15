# AgentPortal — Phase 18: calling Mini.AuthorizationService via the shared client

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
18. Agent Portal calls Mini.AuthorizationService via the shared client ✓
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

## Phase 18 — calling Mini.AuthorizationService via the shared client

### Why this phase

Phase 17 deliberately left Agent Portal a skeleton: login only, no tenant, no downstream call. This
phase gives it a reason to exist — the same reason Phase 16 extracted `AuthorizationClient` out of
SampleApi into `Mini.Infrastructure` in the first place: "a second, real consumer (Phase 17's Agent
Portal) is coming" (see `Mini.Infrastructure/README.md`'s Phase 16 section). That prediction comes
true here. Agent Portal now resolves a tenant and calls `Mini.AuthorizationService`, through the exact
same shared, resilient `IAuthorizationClient` SampleApi already uses — proving the extraction serves a
genuinely independent second caller, not just a second copy of the first one's code path.

Getting there required one more piece first: Agent Portal's tenant resolution. `ITenantContext` /
`TenantContext` / `Tenants` / `TenantResolutionMiddleware` existed only in `MvcClient`'s own
`Infrastructure/MultiTenant` folder — private to that project since Phase 3. The moment a *second*
MVC/BFF client needs the identical claims-based resolution, that's precisely the trigger this repo's
own "shared concerns go in `Mini.Infrastructure`" rule (see its README) calls for, so this phase moves
that folder there first, then wires Agent Portal to it the same way `MvcClient/Program.cs` always has.
`RequireTenantAttribute` moved with it, for the same reason.

### What moved: `Mini.Infrastructure/MultiTenant/`

`Tenant.cs`, `Tenants.cs`, `ITenantContext.cs`, `TenantContext.cs`, `TenantResolutionMiddleware.cs`, and
`RequireTenantAttribute.cs` — out of `MvcClient/Infrastructure/MultiTenant/`, namespace changed from
`MvcClient.Infrastructure.MultiTenant` to `Mini.Infrastructure.MultiTenant`, otherwise byte-identical.
This is a pure extraction, not a behavior change: `MvcClient`'s own `test-phase2.ps1`, `test-api.ps1`,
and `test-multitenancy-external-services.ps1` all pass unmodified against the moved code (see "Things
that broke" below for what it took to get there). See `Mini.Infrastructure/README.md`'s own Phase 18
section for the extraction write-up from that project's side, and `MvcClient/README.md` for a pointer
note.

**What did NOT move, and why**: IdentityServerHost's own `TenantContext.cs`/`Tenants.cs` stay exactly
where Phase 10 left them — they resolve tenant from `acr_values` *before* authentication and treat "no
tenant" as normal, a genuinely different concept from the claims-based `ITenantContext` this phase
shares between MvcClient and Agent Portal (see `Mini.Infrastructure/README.md`'s "The two
`TenantContext`s stay separate" section, still accurate and untouched by this phase).

### Agent Portal's `Program.cs` (new in Phase 18)

```csharp
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<IIdentityContext, IdentityContext>();

builder.Services.Configure<ExternalServicesConfiguration>(builder.Configuration.GetSection("ExternalServicesApi"));
builder.Services.AddHttpClient<IAuthorizationClient, AuthorizationClient>((services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("AuthorizationService");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
       })
       .AddPolicyHandler(ResiliencePolicies.Retry())
       .AddPolicyHandler(ResiliencePolicies.CircuitBreaker());

// oidc options, additions only:
options.Scope.Add("api1");
options.Scope.Add("tenant");
options.ClaimActions.MapUniqueJsonKey("tenant_id", "tenant_id");
options.ClaimActions.MapUniqueJsonKey("role", "role");
```

Same registration shape SampleApi's `Program.cs` already uses for `IAuthorizationClient`, same
`ExternalServicesApi:ServiceDefinitions:AuthorizationService` config section (added to
`appsettings.json`, pointed at `:5015`). The two new scopes and two new `ClaimActions` lines are the
same fix MvcClient's own Phase 3 section already documents for the identical problem — added here
proactively, having already paid for that lesson once.

### `HomeController.CheckAuthorization()`

```csharp
[Authorize]
[RequireTenant]
public async Task<IActionResult> CheckAuthorization()
{
    var accessToken = await HttpContext.GetTokenAsync("access_token");
    var roleFromToken = User.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "Member";
    var context = new Dictionary<string, string>
    {
        { "role", roleFromToken },
        { "caller", identityContext.IdentityType.ToString() }
    };

    var result = await authorizationClient.EvaluateAsync("agent-portal", identityContext, accessToken, context);
    return View("AuthorizationResult", result);
}
```

Mirrors `MvcClient.HomeController.CallApi()` exactly: forward the signed-in user's own access token
(no fresh token fetch), render the result on a view rather than returning JSON. The one deliberate
difference from SampleApi's `/authorize/{resourceName}` endpoint: the resource name is `"agent-portal"`,
not `"sample-api"` — a second, independent resource, with its own seeded policies (see below), so the
two consumers of the shared client are actually distinguishable in a test run rather than one silently
reusing the other's rows.

### `IIdentityContext` for a browser-based caller

The prompt for this phase asked whether `IIdentityContext` (SampleApi's, ported from
`Services.Authorization`) needed a browser-shaped sibling. It doesn't: `IdentityContext.Populate`
already works from any `ClaimsPrincipal`, and `Mini.Infrastructure.Identity.IdentityContextMiddleware`
already does nothing but call it once per request from `context.User`. Agent Portal registers both
exactly as SampleApi does, and its **cookie**-authenticated `ClaimsPrincipal` populates the same
`Subject`/`TenantKey`/`IdentityType` fields a **bearer-token**-authenticated one would — the interface
never assumed a bearer token, only a `ClaimsPrincipal`. No new type, no adapter — see
`Mini.Infrastructure/Identity/IdentityContext.cs`, unchanged by this phase.

### `IdentityServerConfig.json`

```json
{
  "clientId": "agentportal",
  "allowedScopes": [ "openid", "profile", "api1", "tenant" ]
}
```

`api1`/`tenant` added, mirroring `mvcclient`'s entry exactly — the "no downstream API call, no tenant
resolution" line from Phase 17's `allowedScopes` comment is now false, on purpose.

### New seed data: a second resource in `Mini.AuthorizationService`

Two new `Policy` rows, `ResourceName = "agent-portal"`, one per tenant — `acme` requires `Admin`,
`globex` requires `Member`, **mirroring** `"sample-api"`'s existing two policies' required roles exactly
rather than varying them. That was a deliberate choice, not the only reasonable one: varying them (say,
swapping which tenant needs which role) would make a resource-name mix-up impossible to miss, but it
would also mean this phase's test could only ever prove the *negative* case for both tenants (alice is
`Admin`, bob is `Member` — the "sample-api" roles already fit them). Mirroring keeps the roles the
same and still keeps the two resources genuinely distinguishable — by `ResourceName`, `Policy.Name`,
and the exact reason string a decision returns (`"Policy 'Acme Agent Portal Admins' granted access"` is
never confusable with `"Policy 'Acme Admins' granted access"`) — while letting `test-phase18.ps1` assert
a real `authorized: true` decision for both tenants, not just a real `false`.

## Comparison against the real counterparts (Phase 18 additions)

| This sample | Real counterpart | Notes |
| --- | --- | --- |
| Agent Portal calling `Mini.AuthorizationService` via `HomeController.CheckAuthorization()` | Not confirmed against a real downstream-authorization-check call site in `Applications.Apply` — a repo-wide look at `C:\work\Applications.IdentityGateway\docs` (the only `Applications.IdentityGateway`-adjacent path reachable in this environment) turned up architecture and identity-gateway material, not an `Applications.Apply` controller calling `Services.Authorization`. Being honest about the gap rather than guessing: the *shape* here (forward the user's own token, ask a named resource, render/branch on the answer) is modeled on this repo's own SampleApi/Phase-14 pattern, not verified against a specific real Apply call site. | See `SampleApi/docs/identity-context-and-conventions.md` for what *was* confirmed against `Services.Authorization` (the identity/claims conventions this whole chain sits on). |
| `Mini.Infrastructure/MultiTenant`'s extraction | `Applications.Apply`'s own `Infrastructure/MultiTenant` — already the source for MvcClient's Phase-2/3 port | Unchanged comparison; only the sample-side location moved, not the real counterpart being ported from. |

## Where this sample simplifies (Phase 18 additions)

| Real IdG / Apply | This sample (Phase 18) |
| --- | --- |
| A real Apply-like client would likely centralize "is this caller authorized" behind `[Authorize(Policy = "...")]`, resolved by an `IAuthorizationPolicyProvider` that calls out to the authorization service inside the framework's own pipeline (see `Mini.Infrastructure/ExternalServices/AuthorizationClient.cs`'s header comment on `DIT.Authorization.Client`). | Agent Portal calls `EvaluateAsync` explicitly from the controller action, exactly like SampleApi already does — porting the policy-provider integration itself remains future work, not this phase's, same caveat Phase 16 already recorded. |
| A real deployment would likely have a *different* resource per meaningful action Agent Portal exposes, not one hardcoded resource name. | `"agent-portal"` is a single, fixed resource name — there's exactly one authorized action in this skeleton, so one resource is enough to prove the pattern; a second action would need its own resource name and its own seed rows, not a parameter. |

## Things that broke, and why they're worth knowing

**1. `RequestDelegate`/`HttpContext` don't resolve in `Mini.Infrastructure` without an explicit
`using`.** Moving `TenantResolutionMiddleware.cs` into `Mini.Infrastructure` and building immediately
failed with `CS0246: The type or namespace name 'RequestDelegate' could not be found`. The file
compiled fine inside `MvcClient` (an `Sdk.Web` project, whose implicit global usings include
`Microsoft.AspNetCore.Http`) but not inside `Mini.Infrastructure` (a plain `Sdk` class library with a
`FrameworkReference` to `Microsoft.AspNetCore.App` for the *types*, but none of the Web SDK's implicit
usings for the *namespace*). The fix was one explicit `using Microsoft.AspNetCore.Http;` — but the
lesson generalizes: every other file already in `Mini.Infrastructure/Identity` that needs
`HttpContext`/`RequestDelegate` was written with that `using` from the start (nobody had moved a
*middleware* class into this project before); moving `RequireTenantAttribute.cs` next hit the exact
same shape of error one namespace over (`IServiceProvider.GetRequiredService` needs
`Microsoft.Extensions.DependencyInjection`, again implicit in `Sdk.Web`, not in a plain library). A
`FrameworkReference` gets you the assemblies; it does not get you the Web SDK's implicit global usings.

**2. `AuthorizationResult` is ambiguous inside a controller that has `[Authorize]` in scope.**
`HomeController.CheckAuthorization()`'s return type,
`Mini.Infrastructure.ExternalServices.AuthorizationResult`, collided with
`Microsoft.AspNetCore.Authorization.AuthorizationResult` — a real ASP.NET Core type most controllers
never reference directly, but that `using Microsoft.AspNetCore.Authorization;` (needed for the
`[Authorize]` attribute) pulls into scope regardless. `CS0104: 'AuthorizationResult' is an ambiguous
reference` on the very first build. Fixed with a using-alias
(`using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;`) instead of fully
qualifying every `[Authorize]` in the file. Worth knowing for anyone naming a DTO `*Result` in a
project that also does ASP.NET Core authorization — `AuthorizationResult` specifically is already
taken by the framework, and the collision only shows up once both usings are in the same file, which a
narrower, single-purpose controller might never have hit before now.

## What's deliberately missing (as of Phase 18)

- **A policy-provider integration.** `EvaluateAsync` is still called explicitly from the controller
  action, not resolved automatically via `[Authorize(Policy = "...")]` — see "Where this sample
  simplifies" above.
- **More than one authorized action.** Agent Portal has exactly one thing to be authorized for
  (`"agent-portal"`); a second feature would need its own resource name and seed rows, not implied by
  this phase's plumbing.
- **Hostname-based tenant resolution**, same gap `Mini.Infrastructure/MultiTenant/TenantResolutionMiddleware.cs`'s
  own header comment has always named — unaffected by this phase's extraction.

## Try it yourself

Start everything with `.\run-all.ps1`, then:

1. Browse to `https://localhost:5016`, sign in as `alice`/`alice` (Acme, role `Admin`), and click
   **Check authorization for "agent-portal"** on the secure page — expect `Authorized: True`, reason
   naming the `Acme Agent Portal Admins` policy.
2. Sign out, sign back in as `bob`/`bob` (Globex, role `Member`) — the SAME resource, a DIFFERENT
   policy row, still `Authorized: True`.
3. Break it on purpose: in `src/Mini.AuthorizationService/Data/AuthorizationDbContext.cs`, flip Acme's
   `agent-portal` policy's `requiredRoles` from `["Admin"]` to `["Member"]`, delete the two
   `agent-portal` rows from the `MiniAuthorization` database (so `SeedData` re-inserts the edited
   version on next start — it only seeds when the `Policies` table is empty), restart
   `Mini.AuthorizationService`, and re-run `test-phase18.ps1`'s second assertion. Alice (still `Admin`)
   now gets `Authorized: False` for `agent-portal` while `test-phase14.ps1`'s `sample-api` check for the
   same user still passes — proof the two resources' policies really are independent rows, not a shared
   fallback.

Prefer not to click through a browser? [`test-phase18.ps1`](../../test-phase18.ps1) (repo root) drives
the whole thing over raw HTTP: AgentPortal login still works (regression), a real `authorized: true`
decision for both `alice`/acme and `bob`/globex against the SAME `"agent-portal"` resource, and an
anonymous request never reaching the authorization result at all.
