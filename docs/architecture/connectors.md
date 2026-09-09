# Connectors: per-tenant, cascading user sources

**Current state**, as of Phase 12. This describes the system as it is *now* — the phase-by-phase
story is in [IdentityServerHost's README](../../src/IdentityServerHost/README.md).

Before Phase 12, every tenant's `role` claim came from one table in `MiniUsers`. After it, the role
comes from whatever that tenant's rows say it comes from: Acme's own web API, a claim on the incoming
token, or that same table — and moving a tenant between those is a SQL update, not a deployment.

Ported from `DIT.Connectors` (four csprojs in production; one folder,
[`src/Mini.UserService/Connectors/`](../../src/Mini.UserService/Connectors/), here). The library's own
internals are the subject of Series 4 of the DIT library course at
`C:\MyWork\MyLearning\EqusoftInfra` — this doc is about which service calls which, carrying what
token, and what breaks.

## The question the design answers

> For this tenant and this extension point, which integration handles it — and where does it live?

Not "which tenant is this" (that is
[three separate registries](README.md#tenancy-three-registries-that-agree-only-by-convention)) and not
"who is calling" (that is [`IIdentityContext`](service-to-service-auth.md)). This is the layer *after*
both: the tenant is known, the caller is known, and something still has to decide where the data
comes from.

## Who calls whom, as of this phase

```
   browser ──▶ MvcClient / ReactSpa
                     │
                     │ OIDC login
                     ▼
        ┌──────────────────────────┐
        │   IdentityServerHost     │   token issuance: SampleProfileService needs a
        │          :5001           │   "role" claim, and passes tenant_id along
        └────────────┬─────────────┘
                     │  self-issued JWT
                     │  GET /v2/User/identities/role/{sub}?tenant=acme
                     ▼
        ┌──────────────────────────┐
        │     Mini.UserService     │   1. resolve tenant key -> GUID    (ServiceDbContext)
        │          :5013           │   2. which connector(s)?           (CascadingConnectorDbContext)
        │                          │   3. walk the chain
        └────────────┬─────────────┘
                     │
        ┌────────────┼────────────────────────┬─────────────────────────┐
        │ WebApi     │ Claim                  │ AzureGraph              │ (no rows)
        ▼            ▼                        ▼                         ▼
┌───────────────┐  read a claim off      catalog row only,         UserIdentityRoles
│  Mini.AcmeApi │  the CALLER's token    not implemented here      table (the Phase 11
│     :5014     │  (no HTTP at all)      -> failed result          path, unchanged)
└───────────────┘
  Acme's OWN system.
  Service-account token
  (userservice-svc.acme)
  + OriginUserIdentifier
```

**`Mini.AcmeApi` is the first process in this repo standing in for a system a *tenant* owns** rather
than one the platform owns. That distinction is the whole reason connectors exist: the platform cannot
put a table in Acme's datacentre, so it has to be told how to ask.

## The schema: catalog, choice, settings

Three layers, eight tables, in
[`CascadingConnectorDbContext`](../../src/Mini.UserService/Connectors/Data/CascadingConnectorDbContext.cs).
From the real library map: *"catalog says what's possible, choice says what's picked, settings say
where to go."*

| Layer | Table | Holds |
| --- | --- | --- |
| **Catalog** | `Connectors` | one row per integration *type*: `WebApi`, `AzureGraph`, `Claim` |
| | `Handlers` | one row per *extension point*: `GetUserRole`, `GetUserByEmail` |
| | `ConnectorHandlers` | which types **can** serve which points, + the **base** `IsEnabled` |
| **Choice** | `ConnectorHandlerTenants` | tenant × handler → connector, + the **tenant** `IsEnabled` |
| | `ConnectorHandlerCascadingTenants` | the same, **plus `Order`** — try connectors in sequence |
| **Settings** | `WebApiConnectorConfigurations` | tenant → `Host` (one host per tenant) |
| | `WebApiConnectorConfigurationRoutes` | handler → route template, e.g. `users/{id}` |
| | `ClaimConnectorConfigurations` | tenant × handler → `ClaimName` |

Why the split earns three layers: **adding a tenant touches choice + settings only; adding a whole
integration type touches the catalog only.** Each concern has its own lifecycle and, in the real
system, its own import endpoint.

Two `IsEnabled` flags, and **a lookup only succeeds when both are true**. That is a kill switch at two
levels — withdraw an integration from everyone (base) or from one tenant (tenant) — without deleting
anybody's configuration. It also means a vanished connector has two indistinguishable causes, the same
shape as Phase 9's `DynamicIdentityProviderEnabled`.

## The shipped decision table

This is the configuration `run-all.ps1` starts with, seeded through `HasData` in the migration. Every
row is exercised by [`test-phase12.ps1`](../../test-phase12.ps1) and pinned by
`ConnectorResolutionTests`.

| Tenant | `GetUserRole` | `GetUserByEmail` | What a caller sees |
| --- | --- | --- | --- |
| **acme** | **cascading**: 1 WebApi (`:5014`) → 2 Claim (`role`) | single WebApi | alice → `Admin` *from Acme's API*; carol → `Underwriter`; unknown user → 404 from Acme, chain falls to the claim, no claim → `Member` |
| **globex** | single **AzureGraph**, tenant-enabled but **base-disabled** | *no rows* | resolves to nothing → falls back to `UserIdentityRoles` → alice `Admin`, bob `Member`. **The control case: exactly the Phase 11 behaviour.** |
| **initech** | **cascading**: 1 WebApi → 2 Claim | single WebApi | its `Host` is *Acme's* host, which answers 403. Role: absorbed → `Member`, HTTP 200. Email: **502**. |

`alice → Admin` is byte-identical to what the local table says, deliberately, so
[`test-phase7.ps1`](../../test-phase7.ps1) passes unmodified while the *source* of the claim moves —
the same trick Phase 11 played with the tenant GUIDs. **carol is the proof**: she has no row anywhere
in `MiniUsers`, so before this phase she got `Member`, and `Underwriter` exists only in Acme's own
directory.

## The rule that makes "cascading" mean something

In [`ActionHandlerBase`](../../src/Mini.UserService/Connectors/Handlers/ActionHandlerBase.cs):

- **A cascade absorbs failures.** The next link might answer, so the chain continues; a chain that
  ends with no answer reports "no value", not an error. A cascade whose first failure aborted the
  request would have no fallback behaviour at all.
- **A single connector's failure is the outcome.** Nothing to fall back to, so it surfaces — 502.
- **"No such user" is an ANSWER, never a failure**, for either kind. A 404 from a tenant's own API
  means the user is not in their system.

Which produces four distinct outcomes, and the endpoint maps each to a different status because they
mean different things to a caller:

| Outcome | Role endpoint | Email endpoint |
| --- | --- | --- |
| a connector answered | `200` + the value | `200` + the user |
| chain exhausted, nobody knew the user | `200` + `Member` | `404` |
| no enabled row in either choice table | `200` + **local table** | `501` (nobody was asked) |
| single connector failed | `502` | `502` |

The role endpoint's "no connector configured" reads the local table; the email endpoint has no local
table to read. **Same outcome, two meanings** — which is why the handler returns the connector-shaped
result and the endpoint decides the HTTP shape.

## The uncomfortable finding

**A cascade silently absorbs misconfiguration.** Initech's connector row names the wrong tenant's
host — the single most likely connector mistake there is. Acme's API refuses it, the chain moves on,
the claim connector finds nothing, and every Initech user quietly becomes `Member`. HTTP 200. No
login breaks. The only trace is a log line.

The *same* misconfiguration on the *same* tenant against the *same* host surfaces as a 502 on the
email lookup, because that handler is not cascading. Nothing about the fault differs — only whether
something was configured behind it.

And it is not only misconfiguration. Stop `Mini.AcmeApi` and log in as alice: the login succeeds and
her role is `Member`. **A privilege disappeared from a token and nothing reported an error.** For an
authorization-relevant claim, "fall back quietly" is a security posture, not just an availability
one — see the "try it yourself" in
[IdentityServerHost's README](../../src/IdentityServerHost/README.md).

## Authentication on a connector call

A WebApi connector authenticates with the **per-tenant service-account token** Phase 11 introduced —
`userservice-svc.{tenant}`, acquired through `/connect/token` and cached by `TokenClient`. That is the
real system's mechanism, not an invention: *"connector calls to the custom web APIs carry that token,
plus an optional `OriginUserIdentifier` header for user context."*

It also adds a **third** party to
[service-to-service-auth.md](service-to-service-auth.md)'s picture: a resource that belongs to a
tenant while trusting the platform's issuer. Acme validates tokens against `:5001`'s JWKS like every
other API here, and then makes its own decision:

```
              scope "acmeapi"  ──▶  which RESOURCE you may reach   (audience check, 401 if wrong)
              client_id        ──▶  whose DATA within it            (policy check, 403 if wrong)
```

All three `userservice-svc.{tenant}` clients are allowed the `acmeapi` scope, so all three hold a
token Acme *accepts*. Only Acme's own gets past its authorization policy. **A scope cannot express
"this tenant's data"** — which is exactly why Initech's mis-pointed row fails at Acme rather than
succeeding and returning Acme's users to Initech. Acme's policy is the only thing in the whole chain
positioned to catch that.

`OriginUserIdentifier` carries the end user's id so the tenant's own audit log can answer "who looked
at this record" rather than only "userservice-svc.acme did something."

## Where the state lives

| Store | Database | Holds |
| --- | --- | --- |
| `ServiceDbContext` | `MiniUsers` | tenants (key → GUID), `UserIdentityRoles` |
| `CascadingConnectorDbContext` | `MiniUsers` | all eight connector tables |

**Two contexts, one database, two migration histories.** `CascadingConnectorDbContext` writes to
`__EFMigrationsHistory_Connectors`; the default table belongs to `ServiceDbContext`. In production
that arrangement is unavoidable — `DIT.Connectors` is a shared library and brings its own context into
whichever service consumes it.

Sharing one history table would *also* work, which is worth knowing because it is not what you would
guess: with `MigrationsHistoryTable` removed and `MiniUsers` dropped, both migrations apply, both rows
land in the one `__EFMigrationsHistory`, and each context's `dotnet ef migrations list` still reports
only its own — a context's migration set comes from its assembly, not from the table. The separation
is hygiene (one `MigrationId` primary key shared by two independent histories is a latent collision),
not crash avoidance.

The consequence that shows up in the code: **`TenantId` in the connector tables is a bare `Guid` with
no foreign key**, because the `Tenants` table belongs to the other context. EF cannot join across
contexts, so resolving a tenant key to its GUID is a separate round trip by construction. The same
constraint a real service boundary imposes, arriving one layer earlier than expected.

## Where this sample simplifies

| | Real `Services.User` + `DIT.Connectors` | This |
| --- | --- | --- |
| Packaging | four csprojs, dependencies pointing down only (`Data ← Domain ← HTTP ← AspNetCore`) | one folder in the one service that consumes it |
| Connector types | `WebApi`, `AzureGraph` (Microsoft Graph, B2C/Entra), `Claim` | `WebApi` and `Claim` work; `AzureGraph` is a catalog row that answers "not implemented" |
| Configuration import | `AddConnectorsManagement<T>()` / `AddWebApiConnectorsManagement()`: validate → diff → add/delete/update → `ConfigurationResults`, i.e. desired-state reconciliation | `HasData` in a migration |
| Extension points | many (`GetUserDetails`, `SubmitDocument`, users by email, app roles, service principals…) | two |
| Caching | Redis: service principals and app-role assignments on hourly TTLs, distributed locks, tenant-aware key prefixes | none |
| Resilience | `DIT.HTTP.Rest`, config-driven per named client | none on the connectors client, deliberately — see below |
| Result mapping | AutoMapper profiles per tenant shape | Acme's field names happen to match |

## Deliberately missing, and worth knowing

- **No per-host circuit breaker, and that is the real gap.** A dead tenant integration costs ~4.1s per
  login for that tenant. A circuit breaker is precisely the tool, but Polly's breaker state is
  per-policy-instance and `AddPolicyHandler` attaches one instance per named client — so a shared
  breaker would turn one tenant's outage into every tenant's. The fix is a breaker keyed on the request
  host; it is not in this phase.
- **No retry on the connectors client**, and this one is measured. With
  `ResiliencePolicies.Retry()` attached and `:5014` stopped, acme's role lookup took a consistent
  **18.3s** (three attempts at ~4.1s of connect timeout, plus 2s and 4s of backoff) versus **4.1s** for
  one attempt — twelve seconds of pure delay on every login, and the same answer. Retry in front of a
  cascade multiplies the latency the cascade exists to avoid. It belongs where there is nothing behind
  the call, which is where `MvcClient` and `IdentityServerHost` use it.
- **One service-account token serves two callees.** `TokenClient` requests no scope, so a token carries
  every scope its client is allowed — the same bearer converts a user id at `:5001` and reads users at
  `:5014`. A real deployment would want a service account per integration.
- **Duplicate `Order` values in a cascade are unguarded.** The non-cascading table throws on
  ambiguity; two cascading rows sharing an `Order` produce a chain whose order is whatever SQL Server
  feels like, which is the same non-determinism the throw exists to prevent. Named, not fixed.
- **The claim connector cannot answer on the login path**, structurally: the caller is a self-issued
  JWT with no user claims, and the user whose role is being looked up is not the caller. It is
  demonstrated with a purpose-built probe client instead.
