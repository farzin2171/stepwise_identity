# Architecture

**Current state of the system**, as of Phase 10. Cross-cutting docs live here — anything
that names more than one of this repo's projects.

This is deliberately *not* a phase narrative. The per-project READMEs tell the story
phase by phase and describe what things looked like *then*; these docs describe what is
true *now*. When they disagree, the phase README is right about the past and this is
right about the present. Don't "fix" a phase README to match.

- **[README.md](README.md)** (this file) — the map: who runs where, who calls whom, with what token.

Docs that arrive with the phases that need them: `connectors.md` (Phase 12),
`service-to-service-auth.md` (Phase 11), `external-role-providers.md` (Phase 14).

## The processes

| Project | Port | What it is |
| --- | --- | --- |
| [IdentityServerHost](../../src/IdentityServerHost) | 5001 | The authorization server. The mini-IdG proper. |
| [ExternalIdp](../../src/ExternalIdp) | 5011 | A *second*, independent Duende server. Stands in for a partner's IdP. Knows nothing about tenants. |
| [MvcClient](../../src/MvcClient) | 5006 | Server-side confidential client. Stands in for `Applications.Apply`. |
| [SampleApi](../../src/SampleApi) | 5007 | JWT-bearer-protected API. Carries `Services.Authorization`'s identity conventions. |
| [ReactSpa](../../src/ReactSpa) | 5173 | Browser public client. No secret, PKCE only. |
| [ExternalServicesStub](../../src/ExternalServicesStub) | 5012 | Stands in for two sibling DIT services (Tenant Management, User). Superseded in Phase 11. |
| [Mini.Infrastructure](../../src/Mini.Infrastructure) | — | Class library. Shared plumbing, extracted in Phase 10. |
| [ConfigIngestionTool](../../src/Tools/ConfigIngestionTool) | — | Console tool. Writes config into the database. Run manually. |

Start them with [`run-all.ps1`](../../run-all.ps1), which also runs the ingestion tool
first — that ordering matters and used to be prose spread across several READMEs.

## Who calls whom

```
                     ┌──────────────┐         ┌──────────────┐
   browser ─────────▶│  MvcClient   │         │   ReactSpa   │◀───── browser
                     │    :5006     │         │    :5173     │
                     └──┬────────┬──┘         └──┬────────┬──┘
                        │        │               │        │
              OIDC login│        │  user token   │        │ user token
              (confid.) │        │  + svc token  │ (PKCE) │
                        ▼        ▼               ▼        ▼
              ┌───────────────────────┐      ┌──────────────┐
              │  IdentityServerHost   │      │  SampleApi   │
              │        :5001          │      │    :5007     │
              └──┬─────────────┬──────┘      └──────────────┘
                 │             │                     ▲
    federated    │             │ self-issued JWT     │ validates tokens against
    login (OIDC) │             │ at token issuance   │ :5001 discovery + JWKS
                 ▼             ▼                     │ (no call back to :5001
        ┌──────────────┐  ┌──────────────────────┐   │  per request)
        │  ExternalIdp │  │ ExternalServicesStub │   │
        │    :5011     │  │        :5012         │   │
        └──────────────┘  └──────────────────────┘   │
                                                     │
                MvcClient/ReactSpa ──────────────────┘
```

### The four ways a token moves

1. **User login (OIDC authorization code).** Browser → MvcClient or ReactSpa → `:5001`.
   MvcClient is confidential (has a secret); ReactSpa is public (PKCE only, can't keep
   one). Both come back with an ID token and an access token for `api1`.

2. **Federated login.** `:5001` → `:5011`. IdentityServerHost is itself an OIDC *client*
   of ExternalIdp. The result lands on an external cookie which `ExternalController`
   reads once and discards. Two flavours as of Phase 9: file-configured schemes
   (`/signin-external-idp`) and database-backed dynamic ones
   (`/federation/{scheme}/signin`).

3. **Forwarded user token.** MvcClient/ReactSpa → `:5007`, carrying the signed-in user's
   own access token. SampleApi validates it offline against `:5001`'s published JWKS —
   it never calls back per request.

4. **Service-account token.** MvcClient asks `:5001` for a client-credentials token as
   `mvcclient-svc.{tenant}` and calls `:5007` with it — no user involved.
   `IIdentityContext` on the API side tells this caller apart from a real user by the
   *absence* of a `sub` claim.

Separately, and unlike all four: IdentityServerHost calls `:5012` during token issuance
using a **self-issued JWT** (`IIdentityServerTools.IssueClientJwtAsync`), not a registered
OAuth client. Phase 11 replaces that with a real service-account token.

## Tenancy: three registries that agree only by convention

This is the single most important thing to understand about the system, and it is not an
accident of the sample — it mirrors the real architecture.

| Registry | Knows | Populated from |
| --- | --- | --- |
| `IdentityServerHost/Tenants.cs` | key → display name | hardcoded |
| `MvcClient/Infrastructure/MultiTenant/Tenants.cs` | key → `Tenant` object | hardcoded |
| `ExternalServicesStub`'s `tenantsByKey` | key → GUID | hardcoded |

No shared table, no shared code, no shared type. In production, Apply's `Tenants` table
and the IdG's tenant registry are genuinely two independent stores, reconciled by an ops
process. A tenant present in one and missing from another is a real, expected failure
mode.

**It is currently live in this repo.** Phase 9 added `initech` to IdentityServerHost and
to the stub, but *not* to MvcClient. So an Initech user can log in at `:5001` and then be
rejected by MvcClient's `RequireTenantAttribute` with a 401, because MvcClient's own
registry has never heard of them. That's left in place on purpose — see
[Mini.Infrastructure's README](../../src/Mini.Infrastructure/README.md) for why sharing
these would destroy the lesson rather than fix a bug.

### And "tenant" means two different things

Two types named `TenantContext` exist and are **not** interchangeable:

- **IdentityServerHost's** resolves from `acr_values=tenant:<name>` on the query string,
  *before* authentication. It answers "which tenant is this login attempt for." Having no
  tenant is normal.
- **MvcClient's** resolves from the `tenant_id` *claim*, *after* authentication. It
  answers "which tenant is this signed-in user in." Having no tenant, for an
  authenticated user, is a 401.

They sit on opposite sides of the authentication boundary. Phase 10 deliberately did not
merge them; the full comparison table is in
[Mini.Infrastructure's README](../../src/Mini.Infrastructure/README.md).

## State

| Store | Lives in | Holds |
| --- | --- | --- |
| `ConfigurationDbContext` | LocalDB `MiniIdG` | clients, resources, scopes, **identity providers** |
| `PersistedGrantDbContext` | LocalDB `MiniIdG` | authorization codes, refresh tokens, consent |
| `UserDbContext` | LocalDB `MiniIdG` | externally-provisioned identities (`ExternalUserStore`) |
| `TestUsers` | memory | local passwords (alice, bob) — the real IdG has no local login at all |

All three contexts currently share one database. Phase 11 introduces `MiniUsers` and
`MiniAuthorization` as **separate databases**, so that a service genuinely cannot read
another's tables and has to call its API instead.

## Signing keys

IdentityServerHost signs every token with a key chosen by `KeyManagement:Provider` —
`Developer` (a throwaway key, the default, so the sample runs with zero Azure setup) or
`AzureKeyVault`. Everything that validates a token — SampleApi, ExternalServicesStub —
does so against the published JWKS at `:5001`, so a key change propagates without
configuring anything on the consumer side.
