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
Browser  ↔  MvcClient (:5002)  —Bearer token (server-to-server)→  SampleApi (:5003)
Browser  ↔  ReactSpa (:5173)   —Bearer token (browser fetch())──→  SampleApi (:5003)
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
           options.Authority = "http://localhost:5000";
           options.TokenValidationParameters.ValidAudience = "api1";
       });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiScope", policy => policy.RequireClaim("scope", "api1"));
});
```

- **`Authority`** — on the *first* request that needs to validate a token, the JWT
  Bearer middleware fetches `http://localhost:5000/.well-known/openid-configuration`,
  reads `jwks_uri` from it, and downloads IdentityServerHost's public signing key. It
  caches all of this. Every request after that validates the token's signature
  **entirely locally** — no network round trip back to IdentityServerHost per request.
  This is why JWT Bearer scales to many API replicas with no shared session store.
- **`ValidAudience = "api1"`** — must match the `ApiResource` name configured in
  `IdentityServerHost/Config.cs`. Duende stamps that name into every access token's
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
`UserClaims = { "name", "email" }` on the `ApiResource` in
`IdentityServerHost/Config.cs` does — it tells Duende "when a token is issued for
`api1`, also copy these claims onto it, if the user granted the scopes that carry them."

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
origin* (`localhost:5173` calling `localhost:5003`), which makes every request subject
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
dotnet run --urls http://localhost:5003

# in another terminal
curl -i http://localhost:5003/api/v1/identity
# HTTP/1.1 401 Unauthorized
```

Prefer not to click through a browser? Standalone against SampleApi + IdentityServerHost only:
[`../../test-sampleapi-identity-context.ps1`](../../test-sampleapi-identity-context.ps1)
(repo root) drives a user login and a service-account token exchange, then calls both
endpoints above with each — see
[`docs/identity-context-and-conventions.md`](docs/identity-context-and-conventions.md#running-it)
for exactly what it proves.

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
