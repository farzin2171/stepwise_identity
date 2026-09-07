# Mini.UserService

The service that replaced [`ExternalServicesStub`](../ExternalServicesStub) in Phase 11. Same two
routes, same tenant GUIDs, same role table — and behind them a real SQL Server database, an
authenticated management API, and an outbound call of its own.

Runs on **`https://localhost:5013`**. The stub is still on `:5012`, kept and marked superseded.

```
Data/            ServiceDbContext — Tenants, UserIdentityRoles, in their own MiniUsers database
Endpoints/       one file per real controller it stands in for
ExternalServices/IdentityGatewayClient — the call BACK into IdentityServerHost
```

## What it stands in for

Two genuinely separate production services, collapsed into one process:

| Real service | Repo | JWT audience | What this ports |
| --- | --- | --- | --- |
| Tenant Management | `Services.TenantManagement` | `tenantmgntapi` | the read API's `GET /GetByKey/{tenantKey}`, plus a create on the management API |
| User | `Services.User` | `userapi` | `UserController`'s role lookup, `UserIdentityController`'s id conversion, and `ConnectorManagementController`'s idea of a service-account-gated import |

The collapse is the same one the stub made, and it is the one thing this phase deliberately did *not*
fix — a `Mini.TenantService` would teach nothing `Mini.UserService` doesn't already teach. What
survives the collapse is the **audience**: the two surfaces sit behind two authorization policies, so a
token minted for the User service cannot read the tenant registry. In production that boundary is
enforced by the two services being different deployments; here a policy is all that's left of it, so
it's written down explicitly rather than left to nobody calling the wrong route.

## The three things that make it a service rather than a stub

### 1. Its own database

`ServiceDbContext` points at **`MiniUsers`**, not the `MiniIdG` that IdentityServerHost's three
contexts share. That is the substance of "database per service": before this phase, anything wanting a
tenant's GUID could in principle have read it from a table in its own database. Now it cannot — the
rows live somewhere it has no connection string for, so the only way to get them is to call the API.
The constraint is enforced by the absence of a credential, not by a convention.

Baseline rows (three tenants, alice's `Admin`) arrive through `HasData` in the migration, not a startup
seeder. Phase 6 deliberately took row-seeding *out* of IdentityServerHost's startup; adding a second
startup seeder would contradict that. A migration carrying reference data is a different thing from an
app seeding itself at boot.

### 2. A management API, so a tenant can be onboarded without a code edit

Phase 10's "try it yourself" ended by asking which of this repo's three tenant registries should be
authoritative. This is the first one that can be written to at runtime:

```
POST   /api/v1/management/tenants                        { "key": "…", "name": "…" }
DELETE /api/v1/management/tenants/{tenantKey}            hard delete
PUT    /api/v2/management/user/identities/role/{userId}   { "role": "…" }
DELETE /api/v2/management/user/identities/role/{userId}
```

Both deletes are *hard* deletes, mirroring the real management APIs — `Services.TenantManagement`
soft-deletes on its read API (`IsActive = false`) and hard-deletes on its management API, a split its
own analysis flags as possibly unintentional. The read side here honours `IsActive`, so deactivating a
tenant is the more interesting experiment: see the "try it yourself" in
[IdentityServerHost's README](../IdentityServerHost/README.md).

Note what it does **not** fix: `IdentityServerHost/Tenants.cs` and `MvcClient`'s own `Tenants.cs` are
still hardcoded dictionaries. One of three registries became runtime-writable; the drift between them
is fully intact, and still deliberate — see [`docs/architecture/README.md`](../../docs/architecture/README.md).

### 3. It calls IdentityServerHost back

`GET /api/v2/useridentity/convert/{userId}` needs the local↔external identity mapping, which lives in
the IdG's own `UserDbContext` (`ExternalUserStore`, Phase 5) and nowhere else. So this service has the
API surface and none of the data, and has to ask — with a real client-credentials token, as
`userservice-svc.{tenant}`.

That makes the IdG ⇄ User dependency **bidirectional**, exactly as it is in production
(`Services.User`'s `IIdentityClientV1`), with each direction authenticating by a different mechanism.
The full comparison is in
[`docs/architecture/service-to-service-auth.md`](../../docs/architecture/service-to-service-auth.md).

## Where this sample simplifies

| | Real `Services.User` | This |
| --- | --- | --- |
| User sources | per-tenant *cascading connector chain* — Azure AD B2C / Graph, custom web APIs, claims connectors, tried in order with fallback | one `UserIdentityRoles` table |
| Caching | Redis for service principals, app-role assignments and tokens, with distributed locks and tenant-aware key prefixes | none (`IMemoryCache` only for the outbound service-account token) |
| Tenant scoping | every connector configuration is tenant-scoped, resolved through `ITenantContext` | only the outbound service-account secret is per-tenant |
| Health | `/health`, basic-auth gated, checks SQL + Logging service + Authorization service | `/health`, anonymous, `{ status = "healthy" }` |
| Roles | a real identity-role concept fed from Graph app-role assignments | a two-column table with a `"Member"` fallback |
| Guests | `UserClient` short-circuits to `"Guest"` without calling out at all | no guest concept |
| Audit | `AddAudit()` publishes `CREATE_TENANT` / `DELETE_TENANT` events | nothing |

The connector chain is the big one, and it's deliberate: that is Phase 12's subject
(`docs/architecture/connectors.md`), and it's what the DIT library course at
`C:\MyWork\MyLearning\EqusoftInfra` Series 4 teaches from the inside.

## Configuration

`appsettings.Development.json`:

- **`ConnectionStrings:ServiceDb`** — the `MiniUsers` database. Real counterpart:
  `persistence:serviceDb:connectionString`.
- **`Authentication:Authority`** — where to fetch the discovery document and JWKS. Real counterpart:
  `auth:jwt:authority`.
- **`ExternalServicesApi`** — the service registry and the per-tenant service-account secrets, bound to
  `Mini.Infrastructure`'s `ExternalServicesConfiguration`. Real counterpart:
  `services:identityGateway:endpoint` plus `ServiceAccounts:*`.

The three `userservice-svc.{tenant}` clients and `userservice-mgmt-svc` are registered in
[`IdentityServerHost/Configurations/IdentityServerConfig.json`](../IdentityServerHost/Configurations/IdentityServerConfig.json)
and ingested by `ConfigIngestionTool`, like every other client in this sample.

## Verifying it

```powershell
.\run-all.ps1            # starts this service; the stub is no longer in the default set
.\test-phase11.ps1
.\test-phase7.ps1        # must pass UNMODIFIED - that's the real proof
dotnet test tests\StepwiseIdentity.Tests
```

`test-phase7.ps1` was written against the stub. It passing here, with no edits, is what makes
"replacement" a claim rather than a hope.
