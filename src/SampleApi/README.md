# SampleApi — an API protected by the mini IdG

A minimal ASP.NET Core API that trusts nothing by default: every request must carry a
valid `Authorization: Bearer <token>` header, and that token must have been issued by
[`../IdentityServerHost`](../IdentityServerHost) for *this specific API*. This is the
"resource server" side of OAuth — until now, every project in this repo was either the
token *issuer* (IdentityServerHost) or a token *consumer that logs a human in*
(MvcClient). SampleApi is the third role: a service that a client calls *on behalf of*
a logged-in user, using a token that user's login produced.

```
Browser  ↔  MvcClient (:5002)  —Bearer token→  SampleApi (:5003)
                  ↑                                    ↑
             logs the user in                  never talks to IdentityServerHost
             against IdentityServerHost        directly — only downloads its public
             (Authorization Code + PKCE)        signing key once, on first request
```

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

## The one endpoint

```csharp
app.MapGet("/api/identity", (HttpContext ctx) => Results.Ok(new
{
    message = "...",
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("ApiScope");
```

It just echoes back every claim the incoming access token carried, once validation and
the scope policy both pass. That's deliberate: the point of this project isn't the
business logic (there is none) — it's proving, end to end, that a token minted by
IdentityServerHost for a login that happened in MvcClient is independently verifiable by
a completely separate process that has never talked to either of them before.

## Why `name` and `email` show up in the response

By default, an **access token** carries only protocol claims — `sub`, `scope`,
`client_id`, `aud`, and so on — *not* the identity claims (`name`, `email`) that ended
up in the **ID token** via the `profile` scope. An access token and an ID token don't
automatically share claims; each side has to ask for what it needs. That's what
`UserClaims = { "name", "email" }` on the `ApiResource` in
`IdentityServerHost/Config.cs` does — it tells Duende "when a token is issued for
`api1`, also copy these claims onto it, if the user granted the scopes that carry them."

## Running it

This project doesn't do anything on its own — see
[`../MvcClient/README.md`](../MvcClient/README.md#calling-the-api) for how to exercise
it through a real login. Standalone, you can confirm it refuses anonymous traffic:

```bash
cd src/SampleApi
dotnet run --urls http://localhost:5003

# in another terminal
curl -i http://localhost:5003/api/identity
# HTTP/1.1 401 Unauthorized
```

## What's deliberately missing (and why)

- **Any real business data.** One endpoint, no database, no domain logic — this project
  exists to demonstrate token validation, not to be a real API.
- **HTTPS.** `RequireHttpsMetadata = false` is set explicitly and is a local-dev-only
  relaxation — a real API requires HTTPS for both itself and its Authority.
- **Refresh / introspection support.** This API only validates self-contained JWTs. A
  real IdG-protected API sometimes also needs reference-token introspection for
  short-lived, revocable tokens — out of scope for this sample.
