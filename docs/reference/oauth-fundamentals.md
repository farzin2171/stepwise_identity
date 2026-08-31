# OAuth 2.0 fundamentals: the delegation problem, actors, tokens, scopes

Vendor-neutral reference — none of this is specific to Duende IdentityServer, this
repo, or any employer. See [`README.md`](README.md) for how this fits with the rest of
`docs/reference/`.

## The problem OAuth actually solves

Before OAuth, a third-party app that needed access to your data on some other service
had exactly one option: ask for your password on that other service, and log in *as
you*. Early "social login" integrations (circa the mid-2000s) worked exactly this way —
and it was a bad deal for everyone involved, for reasons that have nothing to do with
carelessness and everything to do with what a password fundamentally *is*:

- **All-or-nothing access.** A password doesn't come in a "read my calendar but not my
  email" size. Handing it over hands over everything.
- **No expiry.** A password is good until someone changes it. There's no way to grant
  "access for the next hour" or "access until I say otherwise" without changing the
  password itself.
- **No selective revocation.** If you gave your password to three different apps and
  want to cut off just one, your only tool is to change the password — which breaks the
  other two as well.
- **The app becomes a target.** Every app now stores a copy of your real password,
  multiplying the number of places a breach can leak the one credential that unlocks
  everything.

OAuth 2.0 (RFC 6749) replaces "hand over the password" with **delegated authorization**:
you (the resource owner) tell the service that actually holds your data (the
authorization server) to issue the app a *token* — a credential that's scoped to
specific permissions, expires on its own, and can be revoked individually without
touching anything else. The app never sees your password at all.

## The four actors (RFC 6749 §1.1)

Every OAuth exchange has exactly four parties, and precisely naming them is what makes
the rest of the spec (and every diagram that uses these names) legible:

| Actor | Role | Example |
|---|---|---|
| **Resource Owner** | The person (or entity) who owns the data and can grant access to it | You |
| **Client** | The application requesting access — never touches your password | A mobile banking app |
| **Authorization Server (AS)** | Authenticates the resource owner and issues tokens | The bank's login/identity service |
| **Resource Server (RS)** | Hosts the protected data/API, validates tokens on every request | The bank's account-balance API |

Worked example: you open a budgeting app (**Client**) that wants to read your bank
transactions. It redirects you to your bank's login page (**AS**), where you log in and
approve read-only access. The AS issues a token, and the app presents that token to the
bank's transaction API (**RS**) on every request from then on. The bank's login service
and its transaction API are commonly two different systems — the AS and the RS are
separate roles, even when the same company operates both.

## Tokens: limited-authority credentials, not passwords

An access token is best understood by analogy to a hotel key card, not a house key:

| | Password / master key | OAuth access token / key card |
|---|---|---|
| Scope | Opens everything | Opens specific doors only |
| Expiry | Never, until changed | Expires on its own (often in minutes to hours) |
| Revocation | Changing it breaks every use everywhere | Individually revocable without affecting others |
| If stolen | Full, indefinite compromise | Limited blast radius, limited time window |

Three distinct token types show up across OAuth/OIDC flows — this reference and
[`jwt-and-tokens.md`](jwt-and-tokens.md)/[`openid-connect.md`](openid-connect.md) cover
each in depth:

- **Access token** — short-lived, sent to a Resource Server on every API call. What an
  RS actually checks; see [`jwt-and-tokens.md`](jwt-and-tokens.md).
- **Refresh token** — longer-lived, sent *only* back to the Authorization Server to
  obtain a new access token without re-prompting the user; see
  [`grant-types.md`](grant-types.md).
- **ID token** — an OpenID Connect addition (OAuth itself has no such thing), answering
  *who* logged in rather than *what they're allowed to do*; see
  [`openid-connect.md`](openid-connect.md).

## Scopes: what a token is actually allowed to do

A **scope** is a named permission string the client requests and the resource owner
consents to. The consent screen a user sees is generated directly from the scopes being
requested — it's the literal mechanism behind "this app wants to: read your contacts,
read your calendar."

| Scope | Grants |
|---|---|
| `contacts:read` | Read-only access to contacts |
| `calendar:read` | Read-only access to calendar events |
| `calendar:write` | Create/modify calendar events |
| `email:send` | Send email on the user's behalf |

**Principle of least privilege**: request only the scopes the app actually needs. A
token scoped to `calendar:read` that leaks is a much smaller problem than a token scoped
to everything the user could ever grant — the whole point of scoping tokens narrowly in
the first place.

## See it in this repo

- [`src/IdentityServerHost/Config.cs`'s successor, `Configurations/IdentityServerConfig.json`](../../src/IdentityServerHost/Configurations/IdentityServerConfig.json)
  — `identityResources`/`apiScopes`/`apiResources` are this sample's concrete scopes
  (`openid`, `profile`, `tenant`, `api1`); `IdentityServerHost/README.md`'s Phase 1
  section walks through what each one is for.
- Every client in that same file names its own `allowedScopes` — the allowlist a client
  is *permitted* to request; see `IdentityServerHost/README.md`'s Phase 2 section for
  why that's a separate concern from what a client actually asks for on any given
  login.

## Further reading

- [RFC 6749 §1 — Introduction](https://www.rfc-editor.org/rfc/rfc6749#section-1)
- [RFC 6749 §1.1 — Roles](https://www.rfc-editor.org/rfc/rfc6749#section-1.1)
