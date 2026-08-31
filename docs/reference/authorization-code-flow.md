# The Authorization Code flow, and PKCE

Vendor-neutral reference — builds on [`oauth-fundamentals.md`](oauth-fundamentals.md).
See [`README.md`](README.md) for how this fits with the rest of `docs/reference/`.

## Why not just redirect back with the token directly?

An earlier OAuth flow — **Implicit Flow** — did exactly that: the Authorization Server
redirected straight back to the client with the access token sitting in the URL
fragment. It's now deprecated (prohibited outright by the OAuth 2.0 Security Best
Current Practice, and by the OAuth 2.1 draft), because a token in a URL leaks in ways a
developer doesn't get to control: browser history, server access logs, and the
`Referer` header sent to any page the browser navigates to next.

The Authorization Code flow's answer is a two-step design: a short-lived, single-use
**code** comes back over the browser (front channel); the actual token is fetched
afterward over a direct server-to-server request (back channel) that never touches a
URL bar at all.

## The flow, step by step (RFC 6749 §4.1)

**Step 1 — Authorization request.** The client redirects the browser to the
Authorization Server's `/authorize` endpoint:

```
GET /authorize?
  response_type=code
  &client_id=abc123
  &redirect_uri=https://app.example.com/callback
  &scope=openid profile calendar:read
  &state=xyz789
  &code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM
  &code_challenge_method=S256
```

| Param | Meaning |
|---|---|
| `response_type=code` | Asking for an authorization code, not a token directly |
| `client_id` | Which registered client is asking |
| `redirect_uri` | Where to send the user back — must exactly match what's registered |
| `scope` | What access is being requested |
| `state` | An opaque value the client generates and checks on return — see below |
| `code_challenge`/`code_challenge_method` | PKCE — see below |

**Step 2 — The user authenticates and consents.** Whatever that looks like on the AS's
own login page.

**Step 3 — The AS redirects back with a code.** `GET /callback?code=abc&state=xyz789`.
The code is short-lived (often under a minute), single-use, and — with PKCE in play —
meaningless to anyone but the client that started this exact flow.

**Step 4 — The client exchanges the code for tokens, over the back channel:**

```
POST /token
Content-Type: application/x-www-form-urlencoded

grant_type=authorization_code
&code=abc
&redirect_uri=https://app.example.com/callback
&client_id=abc123
&code_verifier=dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk
```

```json
{
  "access_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 3600,
  "id_token": "eyJ..."
}
```

**Step 5 — The client calls the Resource Server** with `Authorization: Bearer <access_token>`.

The code exists for exactly one reason: to keep the token itself out of anywhere a URL
could end up (history, logs, `Referer`).

## PKCE — Proof Key for Code Exchange (RFC 7636)

A single-page app or mobile app has nowhere safe to keep a `client_secret` — anything
shipped to the browser or bundled into an app binary can be extracted. PKCE replaces a
static secret check with a **per-request, dynamically generated proof**:

1. Before redirecting, the client generates a random `code_verifier` and keeps it in memory.
2. It hashes that verifier (SHA-256, then Base64URL-encoded) into a `code_challenge`,
   sent in the authorization request (Step 1 above).
3. At the token exchange (Step 4), the client sends the original, un-hashed
   `code_verifier`.
4. The AS re-hashes the verifier it just received and checks it matches the
   `code_challenge` from Step 1.

An attacker who intercepts the authorization code in Step 3 still can't redeem it — they
don't have the verifier, only the AS and the legitimate client ever see it, and it's
never sent until the back-channel exchange.

PKCE was originally designed for public clients that can't hold a secret, but it closes
a second, unrelated hole — authorization-code interception on the redirect back to the
client — that affects confidential clients too. That's why the OAuth 2.1 draft makes it
mandatory for **every** client, confidential or not, and why Duende IdentityServer
enforces it by default for new client registrations.

## `state` — CSRF protection

The client generates a random `state` value, stores it (typically server-side or in a
signed cookie) before redirecting, and verifies the value that comes back in Step 3
matches. Without this check, an attacker could initiate their own login, capture the
resulting code, and trick a victim into completing the attacker's login flow instead of
their own — a cross-site request forgery against the login itself, not just a
data-modifying request.

`state` and PKCE's `code_verifier` protect against different attacks: `state` defends
the *redirect back*; PKCE defends the *code exchange*. Both matter, and neither
substitutes for the other.

## See it in this repo

- `mvcclient`'s client registration in
  [`Configurations/IdentityServerConfig.json`](../../src/IdentityServerHost/Configurations/IdentityServerConfig.json)
  sets `requirePkce: true` even though it's a confidential client with a secret — the
  "PKCE closes a second hole too" point above, made concrete.
- `IdentityServerHost/README.md`'s Phase 2 section, **"Three things that broke, and why
  they're worth knowing"**, is this exact flow meeting a real browser: gotcha #1 is the
  correlation/nonce cookies (the ones that make `state`-style CSRF protection work in
  ASP.NET Core's own OIDC handler) failing to survive the round trip over plain HTTP;
  gotcha #2 is `response_mode=form_post` — the library default, and a real cross-origin
  POST carrying the code back, not a redirect with the code in a URL as shown above.
- `IdentityServerHost/README.md`'s **"Try it yourself before moving on"** section
  suggests removing `RequirePkce = true` from `mvcclient` and re-running
  `test-phase2.ps1` — a working solo flow won't visibly break, because PKCE defends
  against a man-in-the-middle scenario a same-machine test can't reproduce. That's
  expected, not a bug in the test.

## Further reading

- [RFC 6749 §4.1 — Authorization Code Grant](https://www.rfc-editor.org/rfc/rfc6749#section-4.1)
- [RFC 7636 — Proof Key for Code Exchange (PKCE)](https://www.rfc-editor.org/rfc/rfc7636)
