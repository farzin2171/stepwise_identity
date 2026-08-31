# UserInfo, discovery, and logout: OpenID Connect in practice

Vendor-neutral reference — builds on [`openid-connect.md`](openid-connect.md). See
[`README.md`](README.md) for how this fits with the rest of `docs/reference/`.

## The UserInfo endpoint (OIDC Core §5.3)

A protected REST endpoint on the Authorization Server. A client calls it with its
**access token** (not the ID token) as a Bearer credential, and gets back the current
claims for the logged-in user:

```
GET /connect/userinfo
Authorization: Bearer eyJ...
```

```json
{
  "sub": "248289761001",
  "name": "Jane Doe",
  "email": "jane@example.com"
}
```

Which claims come back depends on which scopes were granted — same rule as the ID
token itself.

**ID token vs. UserInfo — when to use which:**

| Situation | Use |
|---|---|
| Identity at login time | ID token — already have it, no extra call |
| A large or rarely-needed profile | UserInfo — avoid bloating every token |
| Claims that change between logins | UserInfo — a fresh call gets current data; the ID token is a snapshot from login time |
| Displaying a profile page later in the session | UserInfo |
| Making an authorization decision | **Neither** — that's what the *access token*'s scopes/claims are for, not identity data |

Framework OIDC handlers often support fetching and merging UserInfo automatically
(ASP.NET Core: `options.GetClaimsFromUserInfoEndpoint = true`) — usually simpler and
more consistent than calling the endpoint by hand.

## The discovery document (OIDC Discovery 1.0)

A single well-known URL — `{issuer}/.well-known/openid-configuration` — that lets a
client configure itself from nothing but the issuer's base URL:

| Field | What it points to |
|---|---|
| `issuer` | The canonical issuer identifier — must match every token's `iss` |
| `authorization_endpoint` | Where to send the browser for login |
| `token_endpoint` | Where to exchange a code for tokens |
| `jwks_uri` | Where to fetch signing keys — see [`jwt-and-tokens.md`](jwt-and-tokens.md) |
| `userinfo_endpoint` | The endpoint above |
| `end_session_endpoint` | For logout — see below |
| `scopes_supported` / `claims_supported` | What this server actually offers |

This document is ordinarily world-readable — anyone can view any public OIDC server's
own configuration this way, since none of it is secret; it's how a client bootstraps
itself.

## RP-Initiated Logout (OIDC RP-Initiated Logout 1.0)

Clearing a client application's own session cookie does **not** end the user's session
at the Authorization Server. If the AS session is still alive, the user can navigate to
any other application sharing that same AS and get silently logged back in — the
"partial logout" problem, and a common source of "logout doesn't actually work" bug
reports that are really working exactly as specified, just not as expected.

The actual flow:

1. The client clears its own local session.
2. It redirects the browser to the AS's `end_session_endpoint`, passing an
   `id_token_hint` (the ID token from the session that's ending) and a
   `post_logout_redirect_uri`.
3. The AS ends its own session for that user. It may also fire off a
   **front-channel logout** — a hidden iframe loaded for every other client the user is
   currently logged into — so *their* sessions end too (federated single sign-out).
4. The AS redirects the browser to the `post_logout_redirect_uri`.

`id_token_hint` isn't optional in practice: without it, the AS doesn't reliably know
*whose* session it's being asked to end, and will often show a confirmation prompt
instead of logging out silently.

## Putting it all together — one end-to-end trace

1. **Authorize**: client redirects to `/authorize` with `scope=openid profile`, a
   `nonce`, a `state`, and a PKCE `code_challenge`.
2. **Login**: user authenticates at the AS.
3. **Callback**: AS redirects back with a `code` (and the same `state`).
4. **Token exchange**: client POSTs the code + `code_verifier` to `/token`, gets back an
   access token and an ID token.
5. **(Optional) UserInfo**: client calls `/connect/userinfo` with the access token for
   any claims it didn't already get in the ID token.
6. **API calls**: client presents the access token to whatever Resource Server it needs.
7. **Logout**: client clears its session and redirects to `end_session_endpoint` with
   `id_token_hint`.

Every piece above is covered on its own, in depth, elsewhere in `docs/reference/` —
[`authorization-code-flow.md`](authorization-code-flow.md) for steps 1–4,
[`jwt-and-tokens.md`](jwt-and-tokens.md) for what's inside those tokens,
[`openid-connect.md`](openid-connect.md) for the ID token itself.

## See it in this repo

- Every project in this sample validates its Authority by fetching a discovery document
  — `SampleApi/README.md`'s **"`Authority`"** explanation describes exactly the
  `jwks_uri`-fetch-and-cache behavior above; MvcClient and ReactSpa both point at
  IdentityServerHost's own `https://localhost:5001` the same way.
- `options.GetClaimsFromUserInfoEndpoint = true` is exactly the fix
  `IdentityServerHost/README.md`'s Phase 2 gotcha #3 describes — see
  [`openid-connect.md`](openid-connect.md)'s own cross-reference for the full story of
  why MvcClient needed it.
- **Configured but not exercised in this sample**: every client's
  `postLogoutRedirectUris` in
  [`Configurations/IdentityServerConfig.json`](../../src/IdentityServerHost/Configurations/IdentityServerConfig.json)
  is set up (e.g. `mvcclient` → `https://localhost:5006/signout-callback-oidc`), but
  nothing in `MvcClient/Controllers/HomeController.cs` actually triggers RP-Initiated
  Logout — there's no *Log out* action calling `SignOutAsync` for both the cookie and
  OIDC schemes. Wiring one up (and watching Acme's and Globex's sessions behave
  independently) is a real, open "try it yourself" this repo hasn't done yet.

## Further reading

- [OpenID Connect Core 1.0 §5.3 — UserInfo Endpoint](https://openid.net/specs/openid-connect-core-1_0.html#UserInfo)
- [OpenID Connect Discovery 1.0](https://openid.net/specs/openid-connect-discovery-1_0.html)
- [OpenID Connect RP-Initiated Logout 1.0](https://openid.net/specs/openid-connect-rpinitiated-1_0.html)
