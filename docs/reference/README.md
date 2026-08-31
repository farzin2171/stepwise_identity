# OAuth 2.0 / OpenID Connect reference

A vendor-neutral reference for the protocol concepts this whole repo is built on top
of — genuinely reusable outside `stepwise_identity`, unlike everything else in this
repo. None of it depends on Duende IdentityServer, this codebase, or any particular
employer's systems; each doc's own "Further reading" section points at the actual RFC
or spec it's summarizing, not just this reference's paraphrase of one.

This is **additive** documentation: it doesn't replace or rewrite anything in the
existing phase READMEs (`src/*/README.md`), which explain these same concepts inline,
in narrative form, at the exact moment each one first came up while building this
sample. Read those for "how this repo actually built it, and what broke along the way."
Read this for the underlying concept on its own, cleanly separated from any one
codebase's specific take on it.

## Reading order

Each doc builds on the ones before it — read top to bottom the first time through, or
jump straight to whichever concept you need a refresher on:

1. **[`oauth-fundamentals.md`](oauth-fundamentals.md)** — the problem OAuth solves, the
   four actors, token types, scopes.
2. **[`authorization-code-flow.md`](authorization-code-flow.md)** — the Authorization
   Code grant, step by step, and PKCE.
3. **[`grant-types.md`](grant-types.md)** — confidential vs. public clients, Client
   Credentials, Refresh Token, Device Authorization, and the two flows to avoid.
4. **[`jwt-and-tokens.md`](jwt-and-tokens.md)** — JWT structure, JWKS/key rotation, and
   the validation checklist a Resource Server actually runs.
5. **[`openid-connect.md`](openid-connect.md)** — the identity gap in plain OAuth, the
   ID token, OIDC scopes, `nonce`.
6. **[`oidc-discovery-and-session.md`](oidc-discovery-and-session.md)** — the UserInfo
   endpoint, the discovery document, and RP-Initiated Logout.

## Where this came from

Curated from a personal 66-lesson OAuth/OIDC/Duende curriculum, narrowed to the six
lessons that are genuinely protocol-level and vendor-neutral (the rest of that
curriculum is specific to a particular company's own identity ecosystem, or covers
production/federation patterns — `IdentityProviderStore`-adjacent territory this repo's
own roadmap already tracks separately). Each doc here is freshly written, not a port of
that source material — the lessons served as the topic map, not as text to copy.

## What's deliberately not here

- **Duende IdentityServer's own extension points and conventions** — `IProfileService`,
  the store abstractions, `AddDeveloperSigningCredential()` vs. `AddCertificates()` —
  those are Duende-specific, not OAuth/OIDC-spec-level, and belong in the phase READMEs
  where this repo actually uses them.
- **SAML, Entra ID/Azure AD B2C specifics, BFF (Backend-for-Frontend) patterns, token
  exchange** — real, important topics, out of scope for this first pass (see the
  grilling session that produced this reference: fundamentals first, everything else
  later once this pattern's proven out).
- **This repo's own phase-by-phase build log** — that's what `src/*/README.md` already
  is.
