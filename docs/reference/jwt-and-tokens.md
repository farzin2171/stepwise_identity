# JWT anatomy, JWKS, and how a Resource Server validates a token

Vendor-neutral reference — builds on [`oauth-fundamentals.md`](oauth-fundamentals.md).
See [`README.md`](README.md) for how this fits with the rest of `docs/reference/`.

## The three-part structure

A JSON Web Token (RFC 7519) is three Base64URL-encoded parts joined by dots:

```
header.payload.signature
```

**Signed, not encrypted.** Anyone holding the token can decode the header and payload
without any key — JWT signatures make a token *tamper-proof*, not *confidential*. Never
put secrets or anything sensitive into a claim unless the token is also encrypted
(JWE) — a different, less common mechanism this reference doesn't cover.

## Part 1 — the header

```json
{ "alg": "RS256", "typ": "at+JWT", "kid": "a1b2c3" }
```

- **`alg`** — the signing algorithm. `RS256` (asymmetric — a private key signs, a
  public key verifies) is preferred over symmetric algorithms like `HS256`, which would
  require every Resource Server to hold the *same* secret the Authorization Server
  signs with.
- **`typ`** — the token type. `at+JWT` (RFC 9068) specifically marks a JWT as an OAuth
  access token, distinct from an ID token or an arbitrary JWT.
- **`kid`** — a key ID pointing at which specific key (out of possibly several
  currently-valid ones — see JWKS below) was used to sign this token. This is what
  makes key rotation possible without breaking every token issued moments earlier.

## Part 2 — the payload (claims) (RFC 7519 §4.1)

| Claim | Meaning |
|---|---|
| `iss` | Issuer — which Authorization Server issued this |
| `sub` | Subject — who/what this token is about (absent on a Client Credentials token — no user) |
| `aud` | Audience — which Resource Server(s) this token is valid for |
| `exp` | Expiration time |
| `iat` | Issued-at time |
| `jti` | A unique token ID — enables denylisting/revocation of one specific token |
| `scope` | What this token is allowed to do |

Anything beyond these is a **private claim** — application- or deployment-specific data
riding along on the token (a tenant identifier, a role, whatever a particular system
needs). Duende IdentityServer's `IProfileService` is the seam where an implementation
adds these.

## Part 3 — the signature, and how a Resource Server gets the key

```
signature = RSA_sign(SHA256(base64url(header) + "." + base64url(payload)), AS_private_key)
```

Changing a single byte of the header or payload breaks the signature check. The private
key never leaves the Authorization Server — a Resource Server only ever needs the
**public** half to verify.

**JWKS (JSON Web Key Set)** is how it gets that public key, without any manual
certificate distribution:

1. The RS fetches the AS's discovery document (see
   [`oidc-discovery-and-session.md`](oidc-discovery-and-session.md)) once, and reads its
   `jwks_uri`.
2. It fetches that URL — a JSON list of currently-valid public keys, each tagged with
   its own `kid`.
3. On every token it validates, it matches the token's `kid` header to a key in that
   list.

If a token shows up with a `kid` the RS hasn't seen before, that's the normal signal a
key was rotated — the RS re-fetches the JWKS endpoint (usually cached, with a short TTL)
and finds the new key there. No redeploy, no manual key distribution.

## The validation checklist

A Resource Server that skips any of these isn't really validating the token — it's just
checking that *some* well-formed JWT arrived:

1. **Signature** — verify against the matching JWKS key.
2. **`iss`** — exact match against the Authority this RS trusts.
3. **`aud`** — must contain *this specific* RS's own identifier. Skipping this check is
   the single most common real JWT misconfiguration in practice: a perfectly valid
   token, correctly signed by a trusted issuer, but minted for a *different* API — the
   **confused deputy attack**. A token being valid and a token being valid *for this
   API* are two different questions.
4. **`exp`** — not expired (with a small clock-skew tolerance).
5. **`nbf`** — not used before its stated time, if present.
6. **`scope`** — contains whatever this specific endpoint actually requires.

Framework-provided JWT Bearer middleware (ASP.NET Core's `AddJwtBearer()`, and
equivalents elsewhere) performs checks 1–5 automatically once configured with an
`Authority` and `Audience` — that's what's happening inside that one method call, not
five things to hand-roll yourself. Check 6 is deliberately not part of that same
automatic set in most frameworks: which scope a given endpoint requires is
application-specific, not something generic token-validation middleware could know, so
it's usually a separate authorization check layered on top — a policy, a filter, an
attribute — rather than a configuration flag on the middleware itself.

## See it in this repo

- `SampleApi/README.md`'s **"`Program.cs` — two lines to protect an API"** section is
  this exact checklist made concrete: `options.Authority` drives the JWKS
  fetch-and-cache described above, `ValidAudience = "api1"` is the audience check that
  stops the confused-deputy scenario, and the README names the five checks
  (signature/`exp`/`iss`/`aud`/`nbf`) `[Authorize]` runs automatically before any
  endpoint code executes. The sixth — `scope` — is deliberately *not* part of that same
  automatic set here: it's a separate ASP.NET Core authorization policy
  (`RequireClaim("scope", "api1")`), because "is this a valid token" and "was this token
  issued for what I'm protecting" are different questions with different failure modes.
- `IdentityServerHost/README.md`'s Phase 8 section
  (`AzureKeyVaultKeyStore`/`SigningKeyExtensions`) is `kid`-based rotation in the flesh:
  every enabled certificate version becomes a JWKS validation key; only the newest one
  past a rollover delay becomes the active *signing* key — the same ordering that lets a
  Resource Server pick up a new key as valid before it's ever used to sign anything,
  described above in the abstract.
- `IdentityServerHost/README.md`'s Phase 2 section, "try it yourself," suggests removing
  `"name"` from the `ApiResource`'s `userClaims` and re-running *Call the API* — a
  concrete, hands-on demonstration that every claim riding on an access token got there
  because something asked for it explicitly, never "whatever the user happens to have."

## Further reading

- [RFC 7519 — JSON Web Token (JWT)](https://www.rfc-editor.org/rfc/rfc7519)
- [RFC 9068 — JWT Profile for OAuth 2.0 Access Tokens](https://www.rfc-editor.org/rfc/rfc9068)
- [RFC 7517 — JSON Web Key (JWK)](https://www.rfc-editor.org/rfc/rfc7517)
- [jwt.io](https://jwt.io) — a token decoder, useful for actually looking at one
