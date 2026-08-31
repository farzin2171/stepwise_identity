# Grant types: choosing the right flow

Vendor-neutral reference — builds on
[`authorization-code-flow.md`](authorization-code-flow.md). See [`README.md`](README.md)
for how this fits with the rest of `docs/reference/`.

## Confidential vs. public clients (RFC 6749 §2.1)

Before picking a grant type, one question decides half of it: **can this client keep a
secret?**

| | Confidential client | Public client |
|---|---|---|
| Runs where | A server you control | On the user's device (browser, mobile, CLI) |
| Can hold a `client_secret` | Yes | No — anything shipped to the client can be extracted |
| Examples | An ASP.NET Core web app, a backend microservice | A single-page app, a mobile app, a CLI tool |
| Authenticates itself via | `client_secret` (+ PKCE, defense in depth) | PKCE alone |

A public client can't authenticate itself with a secret it can't keep — PKCE (see
[`authorization-code-flow.md`](authorization-code-flow.md)) is what stands in its
place, not an optional extra.

## The grant types at a glance

| Grant | Status | Use for |
|---|---|---|
| Authorization Code + PKCE | ✅ Use | A human logging in through a browser |
| Client Credentials | ✅ Use | Service-to-service, no user involved |
| Refresh Token | ✅ Use (paired with another grant) | Silent renewal instead of re-prompting the user |
| Device Authorization | ✅ Use | A device with no (usable) browser — CLI, smart TV, IoT |
| Implicit | ❌ Deprecated | Superseded by Authorization Code + PKCE |
| Resource Owner Password Credentials (ROPC) | ❌ Deprecated | Superseded by Authorization Code (even for "internal" tools) |

## Client Credentials grant (RFC 6749 §4.4)

No user, no browser, no redirect, no consent screen — a client authenticates as
*itself* and gets a token representing the client, not a person:

```
POST /token
Content-Type: application/x-www-form-urlencoded

grant_type=client_credentials
&client_id=service-a
&client_secret=***
&scope=api1
```

```json
{
  "access_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 3600
}
```

Notice what's absent: no `refresh_token`, no `id_token`. There's no user session to keep
alive silently — if the token expires, the service just requests a new one with the same
credentials, with no UX cost to anyone. A resulting access token has no `sub` claim
either, since there's no resource owner to name — see
[`jwt-and-tokens.md`](jwt-and-tokens.md) for what that means for a Resource Server
telling a "real user" token apart from a "service" token.

## Refresh Token grant (RFC 6749 §6)

Access tokens are deliberately short-lived (see
[`oauth-fundamentals.md`](oauth-fundamentals.md)); the Refresh Token grant is how a
client gets a new one without re-prompting the user to log in again:

```
POST /token

grant_type=refresh_token
&refresh_token=def456...
&client_id=abc123
```

**Refresh token rotation**, worth knowing: many Authorization Servers invalidate a
refresh token the moment it's used and issue a new one in its place. Reusing an
*already-spent* refresh token is treated as a signal that it was stolen — the whole
family of tokens descended from it gets revoked, not just the one call. A client that
holds onto a stale refresh token after a rotation will find every subsequent refresh
attempt fails; always store the latest one you were issued, not the one you started
with.

## Device Authorization grant (RFC 8628)

For a device that has no browser to redirect through at all, or one too awkward to type
credentials into — a smart TV, a CLI tool, an IoT device. The interaction splits across
*two* devices:

1. The device requests a `device_code` + a short, human-typeable `user_code`, plus a
   `verification_uri`.
2. The device displays the `user_code` and `verification_uri` to the user (e.g. "go to
   example.com/device and enter ABCD-1234").
3. The user opens that URL on *any other device with a browser* (their phone), logs in,
   and approves.
4. Meanwhile, the original device polls the token endpoint until the user finishes step 3,
   then receives its tokens.

This is exactly what the GitHub CLI (`gh auth login`) and streaming apps' TV login
screens do.

## Flows to avoid

**Implicit Flow** returned the access token directly in the redirect URL's fragment —
no code, no back channel. Superseded entirely by Authorization Code + PKCE, and
explicitly prohibited by the OAuth 2.0 Security Best Current Practice and the OAuth 2.1
draft, for the same URL-leakage reasons covered in
[`authorization-code-flow.md`](authorization-code-flow.md).

**Resource Owner Password Credentials (ROPC)** has the client collect the user's raw
username/password directly and trade them for a token. This defeats the entire point of
OAuth — the client sees the password after all — and precludes MFA, federated login,
and phishing-resistant auth entirely. "It's just for an internal tool" is not a good
reason to reach for this; it's exactly the pre-OAuth password-sharing problem
[`oauth-fundamentals.md`](oauth-fundamentals.md) opens with.

## Decision matrix

| Situation | Grant |
|---|---|
| A human logs into a web app | Authorization Code + PKCE |
| Service A calls Service B, no user involved | Client Credentials |
| A CLI tool, smart TV, or IoT device needs to log in | Device Authorization |
| An access token is about to expire | Refresh Token |
| A single-page app calls an API | Authorization Code + PKCE (public client) |
| A mobile app calls an API | Authorization Code + PKCE (public client) |
| A legacy tool collects a password directly | Refactor to Authorization Code — don't reach for ROPC |

## See it in this repo

- `mvcclient` (confidential — has a secret) vs. `reactspa` (public —
  `requireClientSecret: false`) in
  [`Configurations/IdentityServerConfig.json`](../../src/IdentityServerHost/Configurations/IdentityServerConfig.json)
  is the confidential/public distinction made concrete; `IdentityServerHost/README.md`'s
  "ReactSpa — the second client type, and why it looks different" section walks through
  every consequence of that one fact.
- `mvcclient-svc.acme` / `mvcclient-svc.globex` in the same config file are Client
  Credentials clients, one per tenant — see `IdentityServerHost/README.md`'s Phase 2
  section ("two more clients, with no user involved at all") and
  `MvcClient/docs/multitenancy-and-external-services.md` for what actually calls them
  (`ITokenClient`, ported from `Applications.Apply`'s own service-account pattern).
  `test-multitenancy-external-services.ps1` proves a forwarded user token and a
  service-account token produce meaningfully different claims from the same API.
- **Not used anywhere in this sample**: Refresh Token and Device Authorization grants.
  Every login here is short enough that silent renewal was never needed, and nothing in
  this sample runs on a browser-less device. Worth knowing they exist even though this
  repo never exercises them.

## Further reading

- [RFC 6749 §2.1 — Client Types](https://www.rfc-editor.org/rfc/rfc6749#section-2.1)
- [RFC 6749 §4.4 — Client Credentials Grant](https://www.rfc-editor.org/rfc/rfc6749#section-4.4)
- [RFC 6749 §6 — Refreshing an Access Token](https://www.rfc-editor.org/rfc/rfc6749#section-6)
- [RFC 8628 — OAuth 2.0 Device Authorization Grant](https://www.rfc-editor.org/rfc/rfc8628)
