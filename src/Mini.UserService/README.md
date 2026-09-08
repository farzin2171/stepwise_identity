# Mini.UserService

The service that replaced [`ExternalServicesStub`](../ExternalServicesStub) in Phase 11. Same two
routes, same tenant GUIDs, same role table — and behind them a real SQL Server database, an
authenticated management API, and an outbound call of its own.

Runs on **`https://localhost:5013`**. The stub is still on `:5012`, kept and marked superseded.

```
Data/            ServiceDbContext — Tenants, UserIdentityRoles, in their own MiniUsers database
Endpoints/       one file per real controller it stands in for
ExternalServices/IdentityGatewayClient — the call BACK into IdentityServerHost
Connectors/      Phase 12: where a tenant's user data comes from, decided by rows
```

Phase 12 made this the service that answers *"for this tenant, which integration serves this
extension point?"* — the `DIT.Connectors` port. That is a big enough subject to have its own
current-state doc: [`docs/architecture/connectors.md`](../../docs/architecture/connectors.md).

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

## Connectors: a tenant's user source is a row, not a code path (Phase 12)

The role lookup no longer means "read one table." When the caller names a tenant, it means:

1. resolve the tenant key to its GUID (`ServiceDbContext`, filtered on `IsActive`),
2. ask the **choice** layer which connector(s) serve `GetUserRole` for that GUID
   (`CascadingConnectorDbContext`),
3. walk them in `Order` until one answers.

Which produces, from the shipped seed rows: **acme** cascading to
[Acme's own web API](../Mini.AcmeApi) and then to a claim on the incoming token; **globex** resolving
to nothing (its choice row picks a base-disabled connector) and therefore still reading
`UserIdentityRoles`; **initech** cascading into a deliberately mis-pointed host and quietly landing on
the `Member` fallback.

Three things worth carrying away, all covered properly in
[`docs/architecture/connectors.md`](../../docs/architecture/connectors.md):

- **A lookup needs *two* `IsEnabled` flags true** — one on the catalog pairing, one on the tenant's
  choice. So a tenant can be fully configured on paper and still resolve to no connector, with the
  cause a row away in a different table.
- **A cascade absorbs failures; a single connector's failure surfaces.** That is what "cascading"
  means, and the cost is that a cascade hides misconfiguration: initech's role lookup answers 200 with
  `Member` while the *identical* fault on the non-cascading email lookup answers 502.
- **This project's own contexts now disagree by convention.** The connector tables key on the tenant
  GUID and live in a different `DbContext` from `Tenants`, so there is no foreign key and the three
  GUIDs are `HasData` literals in both places.

## Where this sample simplifies

| | Real `Services.User` | This |
| --- | --- | --- |
| Connector packaging | `DIT.Connectors`, four csprojs, dependencies pointing down only | one `Connectors/` folder in this project |
| Connector types | `WebApi`, `AzureGraph` (Graph / B2C / Entra), `Claim` | `WebApi` and `Claim` work; `AzureGraph` is a catalog row that answers "not implemented" |
| Connector config | imported as desired state through `ConnectorManagementController` and reconciled | `HasData` in a migration |
| Extension points | many (`GetUserDetails`, users by email, app roles, service principals…) | two (`GetUserRole`, `GetUserByEmail`) |
| Caching | Redis for service principals, app-role assignments and tokens, with distributed locks and tenant-aware key prefixes | none (`IMemoryCache` only for the outbound service-account token) |
| Tenant scoping | every connector configuration is tenant-scoped, resolved through `ITenantContext` | tenant-scoped too, since Phase 12 — but the tenant is an explicit parameter, not resolved from the caller, because the IdG's self-issued JWT carries no tenant claim |
| Health | `/health`, basic-auth gated, checks SQL + Logging service + Authorization service | `/health`, anonymous, `{ status = "healthy" }` |
| Roles | a real identity-role concept fed from Graph app-role assignments | whatever a tenant's connector answers, or a two-column table, or a `"Member"` fallback — see below |
| Guests | `UserClient` short-circuits to `"Guest"` without calling out at all | no guest concept |
| Audit | `AddAudit()` publishes `CREATE_TENANT` / `DELETE_TENANT` events | nothing |

The connector chain used to be the big one on this list. Phase 12 ported it — the schema, the two
kill-switch flags, the cascade rule and one real WebApi target — so what remains is the layers around
it: no desired-state import API, no Redis, no Graph. `docs/architecture/connectors.md` has the full
current-state picture, and EqusoftInfra Series 4 teaches the library it was ported from, from the
inside.

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
.\run-all.ps1            # starts this service AND Mini.AcmeApi; the stub is not in the default set
.\test-phase11.ps1
.\test-phase12.ps1
.\test-phase7.ps1        # must pass UNMODIFIED - that's the real proof
dotnet test tests\StepwiseIdentity.Tests
```

`test-phase7.ps1` was written against the stub, in Phase 7. It has never been edited, and it asserts
that alice's role is `Admin`. That answer used to come from a `Dictionary` literal in another process;
then from a row in this service's database; and as of Phase 12 it travels out of
[Acme's own web API](../Mini.AcmeApi), over HTTP, authenticated with a per-tenant service-account
token, selected by rows in a table that didn't exist a phase ago. Same assertion, same value, three
mechanisms deep — which is what makes each of those "replacements" a claim rather than a hope.
