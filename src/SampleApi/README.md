# SampleApi — an API protected by the mini IdG

A minimal ASP.NET Core API that trusts nothing by default: every request must carry a
valid `Authorization: Bearer <token>` header, and that token must have been issued by
[`../IdentityServerHost`](../IdentityServerHost) for *this specific API*. This is the
"resource server" side of OAuth — until now, every project in this repo was either the
token *issuer* (IdentityServerHost) or a token *consumer that logs a human in*
(MvcClient). SampleApi is the third role: a service that a client calls *on behalf of*
a logged-in user, using a token that user's login produced.

SampleApi also now carries a port of `Services.Authorization`'s (the real production
DIT authorization-decision service) identity/claims plumbing and API conventions:
[`docs/identity-context-and-conventions.md`](docs/identity-context-and-conventions.md)
— `IIdentityContext`, claims-only multi-tenancy for a caller with no browser, route
versioning, `ProblemDetails`, and a service-account-only endpoint filter, each section
compared against the real code it was ported from.

```
Browser  ↔  MvcClient (:5006)  —Bearer token (server-to-server)→  SampleApi (:5007)
Browser  ↔  ReactSpa (:5173)   —Bearer token (browser fetch())──→  SampleApi (:5007)
                  ↑                                    ↑
             logs the user in                  never talks to IdentityServerHost
             against IdentityServerHost        directly — only downloads its public
             (Authorization Code + PKCE)        signing key once, on first request
```

Two callers, same endpoint, same validation — but they reach SampleApi differently, and
that difference is why this project needs a CORS policy at all (see below).

## `Program.cs` — two lines to protect an API

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           options.Authority = "https://localhost:5001";
           options.TokenValidationParameters.ValidAudience = "api1";
       });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiScope", policy => policy.RequireClaim("scope", "api1"));
});
```

- **`Authority`** — on the *first* request that needs to validate a token, the JWT
  Bearer middleware fetches `https://localhost:5001/.well-known/openid-configuration`,
  reads `jwks_uri` from it, and downloads IdentityServerHost's public signing key. It
  caches all of this. Every request after that validates the token's signature
  **entirely locally** — no network round trip back to IdentityServerHost per request.
  This is why JWT Bearer scales to many API replicas with no shared session store.
- **`ValidAudience = "api1"`** — must match the `apiResources` entry's `name` configured
  in `IdentityServerHost/Configurations/IdentityServerConfig.json` (Phase 6). Duende
  stamps that name into every access token's
  `aud` claim. A token minted for some *other* audience — even a perfectly valid,
  unexpired one — is rejected here before any of this API's own code runs.

Five checks happen automatically, before your endpoint code ever executes: signature,
`exp` (not expired), `iss` (issuer matches Authority), `aud` (audience matches
`ValidAudience`), and `nbf` (not-before). A failed check returns `401 Unauthorized`
with **no application code written for any of it**.

`[Authorize]` (or, for minimal APIs, `.RequireAuthorization()`) only checks "is this a
valid token." It says nothing about *what the token was issued for*. The `"ApiScope"`
policy above adds that second check: the token must also carry a `scope` claim equal to
`api1`. A perfectly valid token — signed by the right server, right audience, not
expired — that was issued for some *other* API still fails this check, because Duende
puts a `scope` claim per requested scope in the token and this one won't have `api1`
among them.

## The identity endpoint — now versioned

```csharp
var api = app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet).HasApiVersion(1.0);

api.MapGet("/identity", (HttpContext ctx, IIdentityContext identityContext) => Results.Ok(new
{
    message = "...",
    identity = new { identityContext.IdentityType, identityContext.Subject, identityContext.ClientId, identityContext.TenantKey },
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("ApiScope");
```

The route is `/api/v1/identity` now, not `/api/identity` — see
[`docs/identity-context-and-conventions.md`](docs/identity-context-and-conventions.md#2-api-versioning--aspversioninghttp)
for the versioning port this came from. It still just echoes back every claim the
incoming access token carried, once validation and the scope policy both pass — that's
deliberate: the point of this project isn't the business logic (there is none) — it's
proving, end to end, that a token minted by IdentityServerHost for a login that
happened in MvcClient is independently verifiable by a completely separate process
that has never talked to either of them before. The one addition, `identity`, is a
port of `Services.Authorization`'s `IIdentityContext` — see the doc above for how it
tells a real user and a service-account caller apart, and how it resolves "which
tenant" purely from claims, with no hostname or UI involved at all.

## The other endpoint — service accounts only

```csharp
api.MapDelete("/admin/cache/{tenantKey}", (string tenantKey) => Results.Ok(new
{
    message = $"Cache cleared for tenant '{tenantKey}' (simulated — this sample has no real cache)."
})).AddEndpointFilter<ServiceAccountOnlyFilter>();
```

Modeled on `Services.Authorization`'s real cache-invalidation endpoint — a
service-to-service call, never a human's. `ServiceAccountOnlyFilter` decides who gets
through entirely on its own, without the formal `[Authorize]`/policy system: no token
at all gets a `401`, a real user's own token (however validly authenticated) gets a
`403`, and only a client-credentials token gets through to the `200`. See the doc above
for the full comparison, including a `ProblemDetails` boundary this port surfaced while
testing it.

## Why `name` and `email` show up in the response

By default, an **access token** carries only protocol claims — `sub`, `scope`,
`client_id`, `aud`, and so on — *not* the identity claims (`name`, `email`) that ended
up in the **ID token** via the `profile` scope. An access token and an ID token don't
automatically share claims; each side has to ask for what it needs. That's what
`"userClaims": [ "name", "email" ]` on the `api1` entry in
`IdentityServerHost/Configurations/IdentityServerConfig.json` (Phase 6) does — it tells
Duende "when a token is issued for `api1`, also copy these claims onto it, if the user
granted the scopes that carry them."

## CORS — needed for ReactSpa, not for MvcClient

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactSpa", policy => policy
        .WithOrigins("http://localhost:5173")
        .AllowAnyMethod()
        .AllowAnyHeader());
});
// ...
app.UseCors("ReactSpa");
```

MvcClient calls this API from server-side C# code — an `HttpClient` running inside the
ASP.NET Core process, not inside a browser. The browser's same-origin policy (and CORS,
which relaxes it) is a browser-enforced rule; server-to-server calls were never subject
to it. ReactSpa calls this API with the browser's own `fetch()`, from a *different
origin* (`localhost:5173` calling `localhost:5007`), which makes every request subject
to CORS. Without this policy, the browser sends a preflight `OPTIONS` request before the
real `GET`, gets no `Access-Control-Allow-Origin` header back, and refuses to send the
real request at all — this API's `[Authorize]`/scope checks never even get a chance to
run, because the browser stops the request before it's fully sent.

`app.UseCors(...)` has to run before `app.UseAuthentication()`/`app.UseAuthorization()`
— the preflight `OPTIONS` request carries no `Authorization` header at all (browsers
never attach one to a preflight), so if CORS ran after authentication, the preflight
itself would get rejected as unauthenticated before ever reaching the CORS middleware
that was supposed to approve it.

## Running it

This project doesn't do anything on its own — see
[`../MvcClient/README.md`](../MvcClient/README.md#calling-the-api) and
[`../ReactSpa/README.md`](../ReactSpa/README.md#calling-the-api) for how to exercise it
through a real login, from each of the two client types this repo has. Standalone, you
can confirm it refuses anonymous traffic:

```bash
cd src/SampleApi
dotnet run --urls https://localhost:5007

# in another terminal
curl -i https://localhost:5007/api/v1/identity
# HTTP/1.1 401 Unauthorized
```

Prefer not to click through a browser? Standalone against SampleApi + IdentityServerHost only:
[`../../test-sampleapi-identity-context.ps1`](../../test-sampleapi-identity-context.ps1)
(repo root) drives a user login and a service-account token exchange, then calls both
endpoints above with each — see
[`docs/identity-context-and-conventions.md`](docs/identity-context-and-conventions.md#running-it)
for exactly what it proves.

## Phase 14 — authorization decisions as a service

Phase 14 adds a new `POST /api/v1/authorize/{resourceName}` endpoint that calls
**Mini.AuthorizationService** (:5015) to evaluate policies. Instead of reading the
`role` claim out of the token to make authorization decisions, the API now asks a
separate service: "is this caller authorized for this resource, given their identity
and request context?"

The endpoint is identical in shape to the real `Services.Authorization`'s `Evaluate`:
caller identity is extracted from the token (claims-only), the service looks up that
tenant's policies, and returns an authorization decision. Authorization can now change
without re-issuing tokens — update a policy row in the database, and the next request
gets the new decision.

Concretely: Phase 13 added Mini.AuthorizationService as a proof-of-concept. Phase 14
integrates it, so now `POST /api/v1/authorize/sample-api` with a valid token will:

1. Extract the caller's identity from the bearer token
2. Call Mini.AuthorizationService's `/api/v1/authorization/evaluate` endpoint
3. Pass the tenant key, resource name, and the caller's role
4. Return a structured response: `{ "authorized": true/false, "reason": "..." }`

The real `Services.Authorization` holds policies in SQL + Redis and serves both an
`Authorize` endpoint (policy-by-name lookup) and an `Evaluate` endpoint (free-form
context). This sample holds policies per resource per tenant and has one endpoint.

Backward compatibility: The `/identity` endpoint is unchanged, still working exactly as
before. The authorization service is opt-in — calling it is a deliberate choice per
request; this phase leaves it as a separate endpoint you can call when you need an
authorization decision that can live outside the token.

Phase 15 note: `IAuthorizationClient.EvaluateAsync` now takes the caller's own bearer token and
forwards it to `Mini.AuthorizationService` as-is, rather than calling with no `Authorization`
header at all (Phase 14's original shape). Mini.AuthorizationService's `/evaluate` requires an
authenticated caller to know *whose* policies to check — without the token, every real call
failed with 401, silently turned into `authorized: false` by `AuthorizationClient`'s error
handling rather than a visible crash. See `src/Mini.AuthorizationService/README.md`'s "Things
that broke" section.

## Phase 15 — a real `admin/cache` endpoint

Phase 14 left `DELETE /admin/cache/{tenantKey}` as a no-op simulation — "this sample has
no real cache" — because at the time, nothing in the system cached anything. Phase 15
gives `Mini.AuthorizationService` a persisted decision cache (`CachedDecisions`), so this
endpoint now has something to actually clear: it forwards the caller's own bearer token
(already proven to be a service account by `ServiceAccountOnlyFilter`, same as before) to
`Mini.AuthorizationService`'s new `DELETE /api/v1/authorization/cache/{tenantKey}`, and
relays back however many rows it cleared. See `src/Mini.AuthorizationService/README.md`'s
Phase 15 section for the cache itself.

## Phase 16 — the authorization client moves to `Mini.Infrastructure`

`AuthorizationClient.cs` (`IAuthorizationClient`, `AuthorizationResult`, `AuthorizationClient`)
no longer lives here. It moved to
[`Mini.Infrastructure/ExternalServices/AuthorizationClient.cs`](../Mini.Infrastructure/README.md#phase-16--a-deliberate-port-begins)
ahead of a second, real consumer arriving (the Agent Portal, Phases 17-18) — the same
"shared plumbing goes in `Mini.Infrastructure`" rule Phase 10 established, applied to code that
had only ever existed once rather than to a duplicate.

Two things changed along with the move, neither of them a new feature — both closing a gap
this project's client had that every *other* named `HttpClient` in this repo already didn't:

1. **Resilience.** The old registration built its own `HttpClient` from a raw
   `HttpClientHandler`, with no `AddHttpClient` and no retry/circuit-breaker policy at all.
   It's now `AddHttpClient<IAuthorizationClient, AuthorizationClient>()` with
   `ResiliencePolicies.Retry()`/`CircuitBreaker()`, the same convention `IdentityServerHost`,
   `MvcClient`, and `Mini.UserService` already used for every one of *their* outbound calls.
   A downed `Mini.AuthorizationService` used to fail on the very first connection attempt;
   now it's retried (2s, then 4s) before giving up, and three failures in a row open a
   30-second circuit exactly like `ExternalServicesStub`'s did back in Phase 9.
2. **Config-driven base address.** `https://localhost:5015` was a literal in this project's
   `Program.cs`. It's now `ExternalServicesConfiguration`'s
   `ServiceDefinitions["AuthorizationService"]` (see `appsettings.json`), the same
   `GetServiceDefinition(...).GetFullPath()` pattern `MvcClient` already uses for its
   `"SampleApi"` client.

The certificate-bypass `HttpClientHandler` the old code built is gone, not replaced — every
other `https://localhost` client in this repo relies on a trusted local dev certificate
(`dotnet dev-certs trust`) instead, and this one is no different.

**Things that broke, proven by actually running it:** `test-phase16.ps1` stops
`Mini.AuthorizationService`, calls `/authorize/sample-api`, and the call now takes noticeably
longer to fail (retry backoff engaging) instead of failing instantly. Restarting the service
right after doesn't immediately fix the next call — the `CircuitBreaker()` trips open for 30
seconds after 3 consecutive failures, so a request made inside that window still gets
`authorized: false` even though the dependency is back. The script waits the window out before
confirming recovery, rather than pretending the breaker doesn't apply here too.

Everything else about this endpoint's behavior is unchanged — `test-phase13.ps1`,
`test-phase14.ps1`, and `test-phase15.ps1` all still pass, unmodified, against the same
`/authorize` and `/evaluate` call path.

## What's deliberately missing (and why)

- **Any real business data.** One endpoint, no database, no domain logic — this project
  exists to demonstrate token validation, not to be a real API.
- **HTTPS.** `RequireHttpsMetadata = false` is set explicitly and is a local-dev-only
  relaxation — a real API requires HTTPS for both itself and its Authority.
- **Refresh / introspection support.** This API only validates self-contained JWTs. A
  real IdG-protected API sometimes also needs reference-token introspection for
  short-lived, revocable tokens — out of scope for this sample.
- **`Services.Authorization`'s actual business logic** (the `Authorize`/`Evaluate`
  endpoints, its SQL Server + Redis policy store, the dynamic
  `IAuthorizationPolicyProvider` that resolves any policy name via a remote call) — see
  [`docs/identity-context-and-conventions.md`](docs/identity-context-and-conventions.md#what-s-deliberately-not-ported)
  for the full list of what was and wasn't ported from it.
