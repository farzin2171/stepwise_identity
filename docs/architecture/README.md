# Architecture

**Current state of the system**, as of Phase 11. Cross-cutting docs live here — anything
that names more than one of this repo's projects.

This is deliberately *not* a phase narrative. The per-project READMEs tell the story
phase by phase and describe what things looked like *then*; these docs describe what is
true *now*. When they disagree, the phase README is right about the past and this is
right about the present. Don't "fix" a phase README to match.

- **[README.md](README.md)** (this file) — the map: who runs where, who calls whom, with what token.
- **[service-to-service-auth.md](service-to-service-auth.md)** — the three ways a process proves who it
  is when there's no user: self-issued JWT, service-account token, Duende local API. Which to use, and
  which one can't be revoked.

Docs that arrive with the phases that need them: `connectors.md` (Phase 12),
`external-role-providers.md` (Phase 14).

## The processes

| Project | Port | What it is |
| --- | --- | --- |
| [IdentityServerHost](../../src/IdentityServerHost) | 5001 | The authorization server. The mini-IdG proper. |
| [ExternalIdp](../../src/ExternalIdp) | 5011 | A *second*, independent Duende server. Stands in for a partner's IdP. Knows nothing about tenants. |
| [MvcClient](../../src/MvcClient) | 5006 | Server-side confidential client. Stands in for `Applications.Apply`. |
| [SampleApi](../../src/SampleApi) | 5007 | JWT-bearer-protected API. Carries `Services.Authorization`'s identity conventions. |
| [ReactSpa](../../src/ReactSpa) | 5173 | Browser public client. No secret, PKCE only. |
| [Mini.UserService](../../src/Mini.UserService) | 5013 | Stands in for two sibling DIT services (Tenant Management, User). Own database, own management API. |
| [ExternalServicesStub](../../src/ExternalServicesStub) | 5012 | **Superseded** by Mini.UserService in Phase 11. Kept, not started by default. |
| [Mini.Infrastructure](../../src/Mini.Infrastructure) | — | Class library. Shared plumbing, extracted in Phase 10. |
| [ConfigIngestionTool](../../src/Tools/ConfigIngestionTool) | — | Console tool. Writes config into the database. Run manually. |
| [StepwiseIdentity.Tests](../../tests/StepwiseIdentity.Tests) | — | The repo's single xunit project. Decision tables only; everything else is a `test-phase*.ps1`. |

Start them with [`run-all.ps1`](../../run-all.ps1), which also runs the ingestion tool
first — that ordering matters and used to be prose spread across several READMEs. Add `-IncludeStub`
to also start the superseded `ExternalServicesStub` for a side-by-side comparison.

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
                 │             │ ▲                   ▲
    federated    │             │ │                   │ validates tokens against
    login (OIDC) │             │ │                   │ :5001 discovery + JWKS
                 │  self-issued│ │service-account    │ (no call back to :5001
                 │  JWT (reads,│ │token (id          │  per request)
                 │  at token   │ │conversion)        │
                 │  issuance)  │ │                   │
                 ▼             ▼ │                   │
        ┌──────────────┐  ┌──────────────────────┐   │
        │  ExternalIdp │  │   Mini.UserService   │   │
        │    :5011     │  │        :5013         │   │
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

4. **Service-account token.** A client-credentials grant against `:5001`, no user involved.
   Three consumers as of Phase 11: MvcClient → `:5007` as `mvcclient-svc.{tenant}`,
   Mini.UserService → `:5001` as `userservice-svc.{tenant}`, and operators → `:5013`'s
   management API as `userservice-mgmt-svc`. `IIdentityContext` tells this caller apart
   from a real user by the *absence* of a `sub` claim.

Separately, and unlike all four: IdentityServerHost calls `:5013` during token issuance
using a **self-issued JWT** (`IIdentityServerTools.IssueClientJwtAsync`), not a registered
OAuth client. Phase 11 did **not** replace that, contrary to what Phase 10 predicted — it
is the exact pattern the real IdG uses, and Phase 11 added the service-account path
alongside it instead, where the real system genuinely has one. All three mechanisms, and
when each is correct, are in
[service-to-service-auth.md](service-to-service-auth.md).

**The IdG ⇄ User dependency is bidirectional as of Phase 11**, as it is in production:
`:5001` calls `:5013` for the role and tenant lookups during token issuance, and `:5013`
calls `:5001` back to convert local ↔ external user ids, because that mapping lives in the
IdG's `UserDbContext` and nowhere else.

## Tenancy: three registries that agree only by convention

This is the single most important thing to understand about the system, and it is not an
accident of the sample — it mirrors the real architecture.

| Registry | Knows | Populated from |
| --- | --- | --- |
| `IdentityServerHost/Tenants.cs` | key → display name | hardcoded |
| `MvcClient/Infrastructure/MultiTenant/Tenants.cs` | key → `Tenant` object | hardcoded |
| `Mini.UserService`'s `Tenants` table | key → GUID, name, `IsActive` | **SQL, writable over HTTP** |

No shared table, no shared code, no shared type. In production, Apply's `Tenants` table
and the IdG's tenant registry are genuinely two independent stores, reconciled by an ops
process. A tenant present in one and missing from another is a real, expected failure
mode.

**It is currently live in this repo.** Phase 9 added `initech` to IdentityServerHost and
to the third registry, but *not* to MvcClient. So an Initech user can log in at `:5001`
and then be rejected by MvcClient's `RequireTenantAttribute` with a 401, because
MvcClient's own registry has never heard of them. That's left in place on purpose — see
[Mini.Infrastructure's README](../../src/Mini.Infrastructure/README.md) for why sharing
these would destroy the lesson rather than fix a bug.

**Phase 11 made the drift worse, deliberately.** The third registry moved from a
`Dictionary` literal to a SQL table with a management API, so a tenant can now be onboarded
there over HTTP with no code edit and no restart — while the other two still need a source
change and a redeploy. Before, all three drifted at the same (slow) speed. Now one of them
can move in seconds and the other two cannot, which is exactly the asymmetry that makes
ops reconciliation hard in the real system.

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
| `ServiceDbContext` | LocalDB **`MiniUsers`** | tenants (key → GUID) and user identity roles |

IdentityServerHost's three contexts share one database; `Mini.UserService`'s is a
**separate** one, added in Phase 11. That separation is the substance of "database per
service": before it, anything wanting a tenant's GUID could in principle have read it from
a table in its own database. Now it cannot — the rows live somewhere it has no connection
string for, so the only way to get them is to call the API. The constraint is enforced by
the absence of a credential, not by a convention.

`MiniAuthorization` arrives with `Mini.AuthorizationService`, not here — Phase 10's
forward-looking note that Phase 11 would introduce both was one phase optimistic.

## Signing keys

IdentityServerHost signs every token with a key chosen by `KeyManagement:Provider` —
`Developer` (a throwaway key, the default, so the sample runs with zero Azure setup) or
`AzureKeyVault`. Everything that validates a token — SampleApi, Mini.UserService — does so
against the published JWKS at `:5001`, so a key change propagates without configuring
anything on the consumer side.

The one exception is IdentityServerHost validating tokens for its *own* API
(`/api/user/convert`): `AddLocalApiAuthentication()` uses IdentityServer's in-process
token validator, so the host never fetches its own discovery document over HTTP from
itself. That also changes the failure code — an insufficient-scope token gets 401 there,
not 403. See [service-to-service-auth.md](service-to-service-auth.md).
