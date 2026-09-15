# Architecture

**Current state of the system**, as of Phase 18. Cross-cutting docs live here — anything
that names more than one of this repo's projects.

This is deliberately *not* a phase narrative. The per-project READMEs tell the story
phase by phase and describe what things looked like *then*; these docs describe what is
true *now*. When they disagree, the phase README is right about the past and this is
right about the present. Don't "fix" a phase README to match.

- **[README.md](README.md)** (this file) — the map: who runs where, who calls whom, with what token.
- **[service-to-service-auth.md](service-to-service-auth.md)** — the three ways a process proves who it
  is when there's no user: self-issued JWT, service-account token, Duende local API. Which to use, and
  which one can't be revoked.
- **[connectors.md](connectors.md)** — where a tenant's user data comes from, decided by rows rather
  than code: the catalog/choice/settings schema, the cascading chain rule, and the finding that a
  cascade silently absorbs misconfiguration.

Docs that arrive with the phases that need them: `external-role-providers.md` (Phase 14).

**Phase 19 added this repo's first Docker dependency and its first message bus.**
`docker-compose.yml` at the repo root starts a single RabbitMQ container (`run-all.ps1` now checks
`docker info` and runs `docker compose up -d`/`down` as part of its normal start/stop). `Mini.Infrastructure/Messaging/` ports `Libraries.Infrastructure/DIT.MessageQueue` as a thin
MassTransit wrapper (`AddMessageBus`, provider-switching `MessageQueueOptions`) and defines the
shared `PolicyChangedEvent` contract type both a future publisher (`Mini.AuthorizationService`,
Phase 21) and future consumer (`Mini.MessageCenter`, Phase 20) will need. No cross-service call
exists yet — Phase 19 proves the bus itself works with a same-process publish/consume round trip
(MassTransit's in-memory test harness in `dotnet test`, and a real-RabbitMQ version in
`test-phase19.ps1`) rather than adding a topic to the diagram below. See
[Mini.Infrastructure's README](../../src/Mini.Infrastructure/README.md)'s Phase 19 section and
[docs/adr/0001-messaging-transport.md](../adr/0001-messaging-transport.md) for the transport
decision.

`AgentPortal` (:5016) gained its first outbound arrow beyond `:5001` in Phase 18 — a call to
`Mini.AuthorizationService` (:5015), forwarding its own signed-in user's access token, through the same
`Mini.Infrastructure`-shared `AuthorizationClient` SampleApi already used since Phase 16. See the
diagram below.

## The processes

| Project | Port | What it is |
| --- | --- | --- |
| [IdentityServerHost](../../src/IdentityServerHost) | 5001 | The authorization server. The mini-IdG proper. |
| [ExternalIdp](../../src/ExternalIdp) | 5011 | A *second*, independent Duende server. Stands in for a partner's IdP. Knows nothing about tenants. |
| [MvcClient](../../src/MvcClient) | 5006 | Server-side confidential client. Stands in for `Applications.Apply`. |
| [AgentPortal](../../src/AgentPortal) | 5016 | Phase 17's second server-side confidential client, its own registration (`agentportal`) on the same IdentityServerHost. Illustrative — not a port of a specific real app — of "more than one MVC/BFF client hits the same IdG," as `Applications.Portal`/`Applications.AdminConsole`/`Applications.CustomerPortal` do alongside `Applications.Apply` in production. Phase 17 was login-only; Phase 18 added tenant resolution (`Mini.Infrastructure`'s shared `ITenantContext`) and a real call to Mini.AuthorizationService for its own `"agent-portal"` resource. |
| [SampleApi](../../src/SampleApi) | 5007 | JWT-bearer-protected API. Carries `Services.Authorization`'s identity conventions. |
| [ReactSpa](../../src/ReactSpa) | 5173 | Browser public client. No secret, PKCE only. |
| [Mini.UserService](../../src/Mini.UserService) | 5013 | Stands in for two sibling DIT services (Tenant Management, User). Own database, own management API, and since Phase 12 the connector machinery that decides where a tenant's users come from. |
| [Mini.AcmeApi](../../src/Mini.AcmeApi) | 5014 | Acme Corporation's **own** user API. The first process here standing in for a system a *tenant* owns, not one the platform owns — a WebApi connector target. |
| [Mini.AuthorizationService](../../src/Mini.AuthorizationService) | 5015 | Out-of-band authorization decisions (Phase 13), called by SampleApi (Phase 14) and, since Phase 18, AgentPortal too, both via `Mini.Infrastructure`'s shared, resilient `AuthorizationClient` (Phase 16). Own database: per-tenant `Policies` — now covering two independent resources, `sample-api` and (Phase 18) `agent-portal` — and since Phase 15 a persisted `CachedDecisions` table. |
| [ExternalServicesStub](../../src/ExternalServicesStub) | 5012 | **Superseded** by Mini.UserService in Phase 11. Kept, not started by default. |
| [Mini.Infrastructure](../../src/Mini.Infrastructure) | — | Class library. Shared plumbing, extracted in Phase 10; since Phase 16 also the landing spot for a deliberate, need-driven port of pieces of `Libraries.Infrastructure` (starting with the authorization-service client). Since Phase 18 also holds `MultiTenant/` — `ITenantContext` and friends, extracted out of MvcClient once AgentPortal became a second, genuine consumer of the identical claims-based resolution. Since Phase 19 also holds `Messaging/` — the MassTransit-based message-bus port (`AddMessageBus`, `PolicyChangedEvent`), with no production publisher or consumer wired to it yet. |
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
              │  IdentityServerHost   │      │  SampleApi   │───┐
              │        :5001          │      │    :5007     │   │ user token,
              └──┬─────────────┬──────┘      └──────────────┘   │ forwarded (Ph.14)
                 │             │ ▲                               ▼
    federated    │             │ │                        ┌──────────────────────────┐
    login (OIDC) │             │ │                        │ Mini.AuthorizationService │
                 │  self-issued│ │service-account          │          :5015           │
                 │  JWT (reads,│ │token (id                └────────────▲─────────────┘
                 │  at token   │ │conversion)                            │ user token,
                 │  issuance)  │ │                                       │ forwarded (Ph.18)
                 ▼             ▼ │                          ┌────────────┴──┐
        ┌──────────────┐  ┌──────────────────────┐          │  AgentPortal  │◀── browser
        │  ExternalIdp │  │   Mini.UserService   │          │     :5016     │
        └──────────────┘  └───────────┬──────────┘          └───────┬───────┘
                                      │                    OIDC login│ (confid., :5001)
                   service-account    │
                   token, per tenant  │
                   (userservice-svc.  │
                    acme) + Origin    ▼
                    UserIdentifier
                          ┌──────────────────────┐
                          │     Mini.AcmeApi     │
                          │        :5014         │
                          │  a TENANT's own API  │
                          └──────────────────────┘
```

`AgentPortal` (right) logs into the same `IdentityServerHost` box `MvcClient` does and, since Phase 18,
forwards its own signed-in user's access token to `Mini.AuthorizationService` — the same service
SampleApi already called since Phase 14, through the same `Mini.Infrastructure`-shared
`AuthorizationClient` (Phase 16). The two consumers ask about two different resource names
(`"sample-api"`, `"agent-portal"`) with independently-seeded policies — see
`Mini.AuthorizationService/README.md` and `AgentPortal/README.md`'s Phase 18 section.

### The four ways a token moves

1. **User login (OIDC authorization code).** Browser → MvcClient, AgentPortal, or ReactSpa →
   `:5001`. MvcClient and AgentPortal are both confidential (each has its own secret, its
   own client registration — Phase 17); ReactSpa is public (PKCE only, can't keep one).
   All three come back with an ID token and an access token for `api1`.

2. **Federated login.** `:5001` → `:5011`. IdentityServerHost is itself an OIDC *client*
   of ExternalIdp. The result lands on an external cookie which `ExternalController`
   reads once and discards. Two flavours as of Phase 9: file-configured schemes
   (`/signin-external-idp`) and database-backed dynamic ones
   (`/federation/{scheme}/signin`).

3. **Forwarded user token.** MvcClient/ReactSpa → `:5007` (SampleApi), and — since Phase 18 —
   AgentPortal → `:5015` (Mini.AuthorizationService) too, all carrying the signed-in user's
   own access token rather than fetching a fresh one. Both callees validate it offline
   against `:5001`'s published JWKS — neither calls back per request.

4. **Service-account token.** A client-credentials grant against `:5001`, no user involved.
   Four consumers as of Phase 12: MvcClient → `:5007` as `mvcclient-svc.{tenant}`,
   Mini.UserService → `:5001` as `userservice-svc.{tenant}`, operators → `:5013`'s
   management API as `userservice-mgmt-svc`, and — new in Phase 12 — Mini.UserService →
   `:5014` as that *same* `userservice-svc.{tenant}`, because a WebApi connector
   authenticates with the tenant's service account. `IIdentityContext` tells this caller
   apart from a real user by the *absence* of a `sub` claim.

   The fourth one is the first time a token in this repo is presented to a resource a
   **tenant** owns rather than the platform. Acme validates it against `:5001`'s JWKS like
   everything else, and then decides for itself: the `acmeapi` scope says which *resource*
   the caller may reach, and `client_id` says whose *data* within it. All three
   `userservice-svc.{tenant}` clients hold a token Acme accepts; only Acme's own gets past
   its policy. See [connectors.md](connectors.md).

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
| `Mini.Infrastructure/MultiTenant/Tenants.cs` (MvcClient's own copy through Phase 17, shared with AgentPortal since Phase 18) | key → `Tenant` object | hardcoded |
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

**Phase 12 added a fourth place a tenant's identity is written down**, and it is not a
fourth registry — it is worse than that. The connector tables key on the tenant **GUID**,
and because they live in a different `DbContext` from the `Tenants` table they cannot have
a foreign key to it, so `CascadingConnectorDbContext` carries the three GUIDs as
`HasData` literals. Two contexts in *one service* over *one database*, agreeing by
convention. The registries at least have the excuse of being different processes.

What keeps it honest is that nothing *resolves* a tenant from those literals: the endpoint
looks the key up in `Tenants` (filtered on `IsActive`) and passes the GUID down, so a
deactivated tenant cannot reach its connectors through a side door. The literals are only
seed data — but they are seed data that will drift the moment a tenant's GUID does.

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
| `CascadingConnectorDbContext` | LocalDB **`MiniUsers`** | the eight connector tables (Phase 12) — catalog, choice, settings |
| `AcmeUsers` | memory | Acme's own employee directory, in `Mini.AcmeApi/Program.cs`. Not this repo's data at all — a `Dictionary` because it stands in for a system we don't own. |
| `AuthorizationDbContext` | LocalDB **`MiniAuthorization`** | per-tenant `Policies` (Phase 13), and since Phase 15 `CachedDecisions` — one row per (tenant, caller, resource, context), with a TTL. |

IdentityServerHost's three contexts share one database; `Mini.UserService`'s two share a
**separate** one — `MiniUsers`, added in Phase 11, with the connector tables joining it in
Phase 12. Two contexts over one database means two migration histories: the connector one
writes to `__EFMigrationsHistory_Connectors`, and the consequence in code is that
`TenantId` in the connector tables is a bare `Guid` with no foreign key, since the
`Tenants` table belongs to the other context. See [connectors.md](connectors.md). That separation is the substance of "database per
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
