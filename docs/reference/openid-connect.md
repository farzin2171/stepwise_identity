# OpenID Connect: adding identity on top of OAuth

Vendor-neutral reference — builds on
[`authorization-code-flow.md`](authorization-code-flow.md) and
[`jwt-and-tokens.md`](jwt-and-tokens.md). See [`README.md`](README.md) for how this fits
with the rest of `docs/reference/`.

## The authentication gap in plain OAuth

OAuth 2.0 is an **authorization** protocol — it proves a client was granted certain
scopes, nothing more. An access token by itself doesn't reliably answer "who is this?":

- The access token may be an **opaque string** with no inspectable structure at all —
  OAuth never required it to be a JWT.
- Even when it is a JWT, its `aud` claim targets the *Resource Server* the client is
  calling, not the client itself — there's no guarantee the client can even read it
  meaningfully.
- There's no standard claim name for "the user's name" or "the user's email" across
  different Authorization Server implementations, even when a token does carry identity
  data.

**OpenID Connect (OIDC)** is a thin, standardized identity layer on top of OAuth that
closes this gap. One sentence covers the whole addition:

> OIDC = OAuth 2.0 + `scope=openid` + an **ID token** in the token response.

Concretely, OIDC adds:

- The `openid` scope, which signals "this is an OIDC request, not just an OAuth one."
- The **ID token** (below).
- An optional **UserInfo endpoint** — see
  [`oidc-discovery-and-session.md`](oidc-discovery-and-session.md).
- A **discovery document** — same reference.

The token response barely changes shape — one extra field:

```diff
 {
   "access_token": "eyJ...",
   "token_type": "Bearer",
   "expires_in": 3600
+  ,"id_token": "eyJ..."
 }
```

## The ID token

A JWT whose entire purpose is telling the *client* who just logged in — never meant to
be sent to an API:

```json
{
  "iss": "https://auth.example.com",
  "sub": "248289761001",
  "aud": "client-abc123",
  "exp": 1700003600,
  "iat": 1700000000,
  "auth_time": 1700000000,
  "nonce": "n-0S6_WzA2Mj",
  "name": "Jane Doe",
  "email": "jane@example.com"
}
```

`sub` is the durable, stable identifier for this user at this issuer — use it as the
key in your own user table, never the email or display name, both of which can change.

## OIDC scopes — requesting identity claims (OIDC Core §5.4)

| Scope | Adds |
|---|---|
| `openid` | Required for any OIDC request; returns `sub` |
| `profile` | `name`, `given_name`, `family_name`, `picture`, etc. |
| `email` | `email` + `email_verified` |
| `phone` | Phone number claims |
| `address` | Postal address claims |
| `offline_access` | Enables a refresh token — not itself an identity scope |

`email_verified` matters more than it looks: it tells you whether the issuer actually
confirmed the address, as opposed to a user typing anything into a profile field. Treat
an unverified email differently from a verified one wherever that distinction matters.

## ID token vs. access token — they are not interchangeable

| | ID token | Access token |
|---|---|---|
| Purpose | Prove identity to the **client** | Authorize a call to a **Resource Server** |
| Audience (`aud`) | The client's own `client_id` | The Resource Server(s) |
| Who reads it | The client | The Resource Server |
| Sent to APIs? | **No — never** | Yes, as the Bearer token |
| Defining spec | OIDC Core | OAuth 2.0 / RFC 9068 |

Sending an ID token to an API instead of an access token is the single most common OIDC
mistake — the audience is wrong (it names the client, not the API), and doing so
sidesteps the entire scope/audience model
[`jwt-and-tokens.md`](jwt-and-tokens.md) describes.

## `nonce` — replay protection for the ID token

Alongside `state` (see [`authorization-code-flow.md`](authorization-code-flow.md)), an
OIDC request also carries a `nonce`: a value the client generates, and which the
Authorization Server binds into the resulting ID token. The client verifies the ID
token's `nonce` matches what it sent. This is a distinct protection from `state`:

- **`state`** defends the *redirect back* — a CSRF check on the authorization response.
- **`nonce`** defends the *ID token itself* — preventing a captured ID token from being
  replayed into a different session than the one that requested it.

Neither substitutes for the other; a correct OIDC implementation sends both.

## See it in this repo

- `new IdentityResources.OpenId()` and `new IdentityResources.Profile()` in this
  sample's Phase 1 `Config.cs` (now
  [`Configurations/IdentityServerConfig.json`](../../src/IdentityServerHost/Configurations/IdentityServerConfig.json)'s
  `{"kind": "OpenId"}`/`{"kind": "Profile"}` entries) are literally these two OIDC
  scopes — `IdentityServerHost/README.md`'s Phase 1 section explains what each one adds.
- `IdentityServerHost/README.md`'s Phase 2 section, gotcha #3 — **"`profile` in scope ≠
  profile claims in the ID token"** — is the "ID token vs. access token" distinction
  above meeting a real, working IdentityServer: for the Authorization Code flow, Duende
  puts only `sub` and protocol-required claims into the ID token by default, expecting a
  confidential client to fetch the rest itself (see
  [`oidc-discovery-and-session.md`](oidc-discovery-and-session.md)'s UserInfo section).
- This sample's `nonce`/`state` correlation cookies are exactly what Phase 2's gotcha #1
  ("Correlation failed") is about — see
  [`authorization-code-flow.md`](authorization-code-flow.md)'s own cross-reference for
  the full story; the ID token's `nonce` claim and ASP.NET Core's correlation cookie are
  two names for closely related plumbing, not two separate concepts to learn.
- **Not exercised in this sample**: `email`/`phone`/`address` scopes, and
  `email_verified`. `alice`'s `TestUser` in `TestUsers.cs` does carry an `email` claim,
  but `IdentityServerHost/README.md`'s "Running it" walkthrough already flags that it
  never shows up on the secure page, precisely because `profile` and `email` are
  separate scopes and only `profile` is requested — adding `new IdentityResources.Email()`
  is named there as exactly the kind of thing worth experimenting with yourself.

## Further reading

- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- ["OAuth 2.0 is not an authentication protocol"](https://oauth.net/articles/authentication/) — oauth.net
