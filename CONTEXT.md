# stepwise_identity

A from-scratch teaching port of `Applications.IdentityGateway`, built one phase at a
time — this file is the glossary for this sample's *own* invented vocabulary. Standard
OAuth/OIDC terms (client, scope, resource, grant type, subject) are covered in
[`docs/reference/`](docs/reference/README.md) and are deliberately not repeated here.

## Language

**Phase**:
A numbered, incremental teaching unit this course is built from — each one a small,
runnable slice, its own README section explaining what problem it solves and comparing
the result against the real IdG.
_Avoid_: Step, milestone, iteration.

**mini-IdG**:
Nickname for this whole sample — `IdentityServerHost` plus the client apps that log
into it (`MvcClient`, `ReactSpa`, `SampleApi`, `ExternalIdp`) — as a unit, distinct from
the real production system.

Note what Phase 12 put *outside* it: `Mini.AcmeApi` stands in for a system a **tenant** owns, so it
lives in the repo without being part of the mini-IdG. That line matters the moment someone says
"everything here validates tokens against `:5001`" — Acme does too, and it still isn't ours.
_Avoid_: "this sample" alone when the distinction from the real IdG matters.

**real IdG**:
Shorthand for `Applications.IdentityGateway`, the production system this course ports
from and compares against in every phase.
_Avoid_: "the real system," "production," "IdentityGateway" alone — ambiguous with this
sample's own `IdentityServerHost`.

**TenantContext** (IdentityServerHost):
The scoped, per-request object `TenantResolutionMiddleware` populates from
`acr_values=tenant:<name>` (or the re-encoded `ReturnUrl`), read by `AccountController`
to reject a login whose credentials don't match the requested tenant.
_Avoid_: "tenant" alone — see `ITenantContext` below for the other, unrelated resolution.

**ITenantContext**:
A tenant abstraction ported from `Applications.Apply` — resolves from the `tenant_id`
*claim* on the already-signed-in user, not a query parameter. A different mechanism for a
related concept than IdentityServerHost's `TenantContext`; the two `TenantContext`s don't
share code or a type (see that entry below).

Lived only in `MvcClient/Infrastructure/MultiTenant` through Phase 17. Moved to
`Mini.Infrastructure/MultiTenant` in Phase 18, when `AgentPortal` became a second, genuine
consumer of the identical claims-based resolution — a pure extraction (same types, same
behavior, only the namespace changed), not a design change. Both `MvcClient` and
`AgentPortal` now share the SAME `Tenants.All` dictionary rather than each keeping its own
copy — see `Mini.Infrastructure/README.md`'s Phase 18 section for why this one, unlike the
three *tenant registries* below, was a shared concept all along rather than two things
wearing the same name.

**Confirmed in Phase 10**, which set out to merge them and concluded it shouldn't. They are
not one abstraction with two resolvers: IdentityServerHost's resolves *before*
authentication and treats "no tenant" as normal (`test-phase3.ps1` §4 depends on that);
MvcClient's resolves *after* authentication and treats "no tenant" as a 401 (via
`RequireTenantAttribute`). They disagree about whether the absence of a tenant is an error,
which is the invariant that would have to be shared for one type to serve both. Full
comparison table in [`src/Mini.Infrastructure/README.md`](src/Mini.Infrastructure/README.md).
_Avoid_: "the tenant context" unqualified — always name which project's.

**Mini.Infrastructure**:
The single-csproj class library holding the plumbing more than one project in this repo
consumes: `Identity/` (`IIdentityContext` and friends), `ExternalServices/` (`TokenClient`,
the service registry, and — since Phase 16 — `AuthorizationClient`), `Http/`
(`ResiliencePolicies`), and — since Phase 18 — `MultiTenant/` (`ITenantContext` and
friends, extracted out of MvcClient once AgentPortal became a second real consumer).
Created in Phase 10 by *extracting* existing duplicates, not by designing a library up
front.

Distinct from the `MyCompany.*` mini-libraries in the DIT library course at
`C:\MyWork\MyLearning\EqusoftInfra`: those exist to teach how a DIT library is *built*
internally; this exists to be the thing this repo's own apps actually consume. When a file
here needs to explain a real DIT library's internals, it links to that course.

**Its relationship to `Libraries.Infrastructure` changed in Phase 16.** Through Phase 15 this
was pure de-duplication — code that already existed in two or three places, collapsed into
one — and calling it a port of `Libraries.Infrastructure` was wrong (most of that library had,
and still has, no counterpart here). Phase 16 added something that did *not* already exist
twice: `AuthorizationClient`, moved out of `SampleApi` ahead of a second real consumer (the
Agent Portal, Phase 17-18), loosely modeled on `Libraries.Infrastructure/DIT.Authorization.Client`.
That makes this a de-duplication library that has *also*, since Phase 16, become the deliberate
landing spot for a partial, need-driven port — never a whole `DIT.*` project moved over intact,
and never ahead of a real consumer. See `src/Mini.Infrastructure/README.md`'s Phase 16 section.
_Avoid_: "the mini DIT libraries" (still wrong — this isn't the from-scratch DIT-library-internals
course, `EqusoftInfra` is), and don't assume every `DIT.*` area eventually lands here — only the
ones an app in this repo actually ends up needing.

**Tenant registry**:
A per-application store of which tenants exist. There are **three**, and they share no
code, table, or type: `IdentityServerHost/Tenants.cs` (key → display name),
`Mini.Infrastructure/MultiTenant/Tenants.cs` (key → `Tenant`; `MvcClient`'s own copy through Phase 17,
moved in Phase 18 and now also read by `AgentPortal` — see `ITenantContext` above), and — as of Phase 11 —
`Mini.UserService`'s `Tenants` **table** (key → GUID, name, `IsActive`). They agree only by
convention, mirroring the real system, where Apply's `Tenants` table and the IdG's registry
are independent stores reconciled by an ops process.

The drift is currently live and deliberate: `initech` (Phase 9) exists in two of the three,
so an Initech user can log in at IdentityServerHost and then be rejected by MvcClient.

Phase 11 made it **asymmetric** as well as live: the third registry can now be written over
HTTP (`POST /api/v1/management/tenants`) with no code edit and no restart, while the other two
still need a source change and a redeploy. Before, all three drifted at the same slow speed.

Phase 12 added a **fourth place a tenant's identity is written down**, which is not a fourth registry
and is arguably worse: `CascadingConnectorDbContext` carries the three tenant GUIDs as `HasData`
literals, because the `Tenants` table lives in a different `DbContext` and a foreign key is therefore
impossible. Two contexts, one service, one database, agreeing by convention. Nothing *resolves* a
tenant from those literals — the endpoint looks the key up in `Tenants` filtered on `IsActive` and
passes the GUID down, so a deactivated tenant cannot reach its connectors through a side door — but
they will drift the moment a tenant's GUID does.
_Avoid_: "the tenant list" — there isn't one. And don't count the connector tables as a registry:
they say what a known tenant uses, never which tenants exist.

**Tenants.cs**:
The hardcoded tenant-key → display-name dictionary (plus `ResolveTenantKey`, parsing
*which* tenant a login is for from `acr_values`). A separate concern from `TenantClient`
below, which resolves that same key to a GUID over HTTP — the two aren't a "not yet
ported" pair, they were never the same thing.

**ExternalUserStore**:
The first-login provisioning store for federated identities: persists what a federated
login provisioned (`name`, `tenant_id`) so `SampleProfileService` can look it up at
token-issuance time, since IdentityServer's own `context.Subject` doesn't carry it.
SQL-backed (via `UserDbContext`) as of Phase 5.
_Avoid_: "user store" alone — ambiguous with `TestUserStore`, Duende's own local-password
store.

**EcosystemTenant**:
The field naming which tenant an external provider belongs to — a config field on each
entry under the `ExternalProviders` section, or (as of Phase 9) a key in a database-backed
provider's `Properties` bag. Either way it's read through `IAuthenticationOptions`.

Corrected in Phase 9: this entry used to say the real IdG "resolves this from the *scheme
name* instead." That's wrong as stated. The real IdG's database rows carry an explicit
`EcosystemTenant` property, exactly like this sample's (see the real repo's
`config/identityProviders.json`). The genuine difference is elsewhere — the real IdG
doesn't use `EcosystemTenant` to decide *which providers a tenant's login page shows*. It
asks the client instead, via `IdentityProviderRestrictions` plus a client property named
after the tenant holding an ordered scheme list. That difference is real and load-bearing
(it lets two tenants share one provider, which this sample can't).
_Avoid_: "tenant" alone.

**Dynamic provider**:
An external identity provider that doesn't exist as a registered authentication scheme at
startup — it's a row in the `IdentityProviders` table, resolved by `IdentityProviderStore`
on the request that needs it and served under `/federation/{scheme}/`. Duende's own term.
Contrast with the Phase 4 file-based providers, which are registered once at startup from
`appsettings.json` and choose their own `CallbackPath`.
_Avoid_: "database provider," "DB-backed IdP" — and don't say "external provider" alone
when the distinction from the file-based kind matters.

**IdentityProviderStore** (IdentityServerHost):
This sample's subclass of Duende's EF `IdentityProviderStore`, overriding only `MapIdp` to
turn a row's `Type` column into a strongly-typed provider (`OpenIdConnectProvider`) instead
of Duende's generic `OidcProvider`. The one custom store the real IdG has, and the only one
this sample ports — `IClientStore`/`IResourceStore` are Duende's stock EF versions in both.
_Avoid_: confusing with `ExternalUserStore` (provisioned federated *identities*) or
`TestUserStore` (local passwords). This one stores *providers*, not users.

**DynamicIdentityProviderEnabled**:
The config flag gating whether database-backed providers can actually serve a login. Same
name and placement as the real IdG's. Note what it does *not* gate: `IdentityProviderStore`
is registered either way. Off means the store exists and the rows exist but nothing reads
them — so a vanished login button has two indistinguishable causes (this flag, or the row's
own `Enabled` column).

**initech**:
The third tenant, added in Phase 9. Has no local test user and no `ExternalProviders`
entry — its only way in is a database-backed provider, which makes it the control case
proving a row alone can produce a working federated login. `acme` (file-based providers)
and `globex` (none) keep their Phase 4 behavior untouched.
_Avoid_: treating it as evidence that tenant onboarding is codeless — `Tenants.DisplayNames`
is still a hardcoded dictionary. Phase 11 changed only the third registry: a NEW tenant's row
in `Mini.UserService` can now be created over HTTP, but its display name still needs a code
edit and a redeploy.

**Service account**:
A client-credentials-grant client (e.g. `mvcclient-svc.acme`), usually one per tenant, for
server-to-server calls with no user or browser involved — ported from `Apply`'s pattern.

Phase 11 added four: `userservice-svc.{acme,globex,initech}` (Mini.UserService calling back into
IdentityServerHost) and `userservice-mgmt-svc` (writing to Mini.UserService's management API).

Phase 12 gave all three `userservice-svc.*` clients the `acmeapi` scope as well, because a `WebApi`
`Connector` authenticates with the tenant's service account — the real mechanism, not an invention.
Two consequences worth holding onto: one cached token now serves two callees (`TokenClient` requests
no scope, so a token carries every scope its client is allowed), and *all three* tenants hold a token
`Mini.AcmeApi` accepts, so its own `client_id` policy is what actually keeps Initech out.
The last one has NO tenant suffix, on purpose — creating a tenant is inherently cross-tenant,
so `IIdentityContext` resolves its `TenantKey` to null. This sample's suffix convention cannot
express "all tenants" as distinct from "none"; the real `DIT.Identity` reads an explicit
`service_tenant` claim, which can simply be absent.
_Avoid_: "service client," "machine user" — and don't use it for a self-issued JWT, which is a
different mechanism entirely (see above).

**IIdentityContext / IdentityType**:
The claims-only abstraction over "who is calling" — a `User` identity (has a `sub`, tenant from
the `tenant_id` claim) vs. a `Service` identity (no `sub`, tenant parsed from the `client_id`
suffix instead) — ported from `Services.Authorization`, and in `Mini.Infrastructure/Identity`
since Phase 10. Consumed by SampleApi and, since Phase 11, `Mini.UserService`.

**Two types, three kinds of caller**, found in Phase 11 and left unfixed on purpose. A user, a
registered service account, and IdentityServerHost issuing itself a token are three genuinely
different callers, but the last two are indistinguishable here because both are identified by
the *absence* of `sub`. So `ServiceAccountOnlyFilter`, which decides purely on `IdentityType`,
cannot tell a revocable credential from a self-signed one — verified by pointing `UserClient`
at Mini.UserService's `/api/v2/identity` diagnostic. The real `DIT.Identity` distinguishes
caller kinds explicitly with a `service_isService` claim, and models four rather than two.
`IdentityContextTests` pins the current, weaker behaviour so a later fix can't land silently.
_Avoid_: "caller," "principal" alone — and don't read `IdentityType.Service` as "a registered
service account," which is what it does not quite mean.

**IdentityServerConfig.json**:
The JSON file holding Clients/Resources/Scopes — config as data, not compiled code.
Replaced `Config.cs` outright in Phase 6 (deleted, not deprecated). Read only by
`ConfigIngestionTool`, never by IdentityServerHost itself.

**ConfigIngestionTool**:
The standalone console tool (`src/Tools/ConfigIngestionTool`) that writes
`IdentityServerConfig.json` into `ConfigurationDbContext` — this course's own stand-in
for the real IdG's since-deleted Data Ingestion Tool
(`IdentityGatewayConfigurationExporter`). Run explicitly and separately from
`dotnet run`ning IdentityServerHost, never automatically at its startup.
_Avoid_: "the seed step" — `SeedData` (IdentityServerHost) only migrates schema now; it
doesn't seed rows as of Phase 6.

**TenantClient** / **UserClient** (IdentityServerHost):
Ported in Phase 7. HTTP clients to `Mini.UserService` (`ExternalServicesStub` until
Phase 11), authenticated with a self-issued JWT
(`IIdentityServerTools.IssueClientJwtAsync`) rather than a registered OAuth client. `TenantClient.GetTenantAsync` resolves a tenant *key* ("acme") to its
`tenant_guid` claim, cached forever on purpose (the real system's own bug, reproduced
here). `UserClient.GetRoleAsync` resolves a `role` claim, never cached — the deliberate
contrast. Both called from `SampleProfileService`, not a separate component — see
`Mini.UserService` below. Only the URL changed in Phase 11: same routes, same GUIDs, same
fallback, so `test-phase7.ps1` passes unmodified against the replacement.
_Avoid_: confusing this with `Tenants.cs` — that resolves *which* tenant a login is for
(from `acr_values`); `TenantClient` only resolves *that* tenant's GUID, a separate,
downstream, additive step.

**tenant_guid**:
The claim `TenantClient` adds, holding the GUID `Mini.UserService` resolved from the
`tenant_id` claim's key. Additive, not a replacement — `tenant_id` still holds the
friendly key (Phase 3's shape), since MvcClient/SampleApi's tenant resolution both
already depend on that shape.

**role**:
The claim `UserClient` adds, holding whatever `Mini.UserService` returns for a
subject id — never cached. Exists purely to contrast with `tenant_guid`'s cached (and
deliberately broken) lookup; not a real permissions/roles system.

**Where it comes from changed in Phase 12** while the claim did not: `UserClient` now sends
`?tenant=`, and `Mini.UserService` resolves the value through that tenant's `Connector` chain instead
of reading one table. For `acme` it comes out of `Mini.AcmeApi`; for `globex`, still the
`UserIdentityRoles` table; for anyone whose chain answers nothing, the `"Member"` fallback. Nothing in
`IdentityServerHost` knows which.
_Avoid_: reading a `role` claim as authoritative — a cascade that fails silently downgrades it to
`"Member"`, and nothing reports an error (see `Connector`).

**ExternalServicesStub**:
Phase 7's stand-in for the real IdG's two sibling DIT microservices (Tenant Management API,
User API), collapsed into one process, each route backed by a `Dictionary` literal. Validates
the self-issued JWTs `TenantClient`/`UserClient` send by trusting IdentityServerHost's own
signing key — nothing else to configure.

**Superseded by `Mini.UserService` in Phase 11**, and kept — the reference case for this
repo's rule that a replaced artifact stays in the tree and stays marked. Nothing calls it now
(`ExternalServicesApi` points at `:5013`) and `run-all.ps1` starts it only with
`-IncludeStub`.
_Avoid_: confusing with `ExternalIdp` — that's a stand-in for an external *identity*
provider (a login source); this is a stand-in for backend *data* services IdentityServerHost
calls at token-issuance time, never involved in authentication itself.

**Mini.UserService**:
The Phase 11 replacement for `ExternalServicesStub` (`:5013`) — same two routes, same tenant
GUIDs, same role fallback, backed by its own `MiniUsers` database. Still collapses the two
real services into one process, but keeps their two JWT audiences (`tenantmgntapi`, `userapi`)
as separate authorization policies, so the boundary two deployments would enforce is at least
written down. Adds two things the stub had no version of: a service-account-gated management
API (a tenant can be onboarded over HTTP, no code edit) and an outbound call back into
IdentityServerHost.

Phase 12 made it the service that decides **where a tenant's user data comes from**, via the
`Connector` machinery in `Connectors/` and a third route (`GET /api/v2/User/identities/email`). The
`role` route gained an *optional* `?tenant=` parameter, and optional is load-bearing: omitting it is
the pre-Phase-12 path, which is what lets `test-phase7.ps1` and `test-phase11.ps1` keep exercising
the local table unchanged.
_Avoid_: "the user service" unqualified when the real `Services.User` is also in scope — and
don't call it a port of `Services.User` alone, since half of it is `Services.TenantManagement`.

**MiniUsers**:
`Mini.UserService`'s own LocalDB database, separate from the `MiniIdG` that
IdentityServerHost's three contexts share. The separation is the point of Phase 11: no other
process has a connection string for it, so "database per service" is enforced by the absence
of a credential rather than by convention.

Holds **two** contexts as of Phase 12 — `ServiceDbContext` and `CascadingConnectorDbContext` — with
separate migration histories (`__EFMigrationsHistory` and `__EFMigrationsHistory_Connectors`). The
separation is hygiene, not crash avoidance: sharing one table was tested and works fine, which is the
opposite of what it looks like.
_Avoid_: assuming `MiniAuthorization` exists — it arrives with `Mini.AuthorizationService`,
not in Phase 11, despite a Phase 10 note that said otherwise.

**Identity conversion**:
Translating between a local subject id (`external:{scheme}:{externalSubjectId}`) and the
external provider's own subject id, in either direction — `convertTo=Local|External`. Ported
in Phase 11 as `UserConversionController` plus the pure `IdentityConversion` decision table.
Owned by IdentityServerHost because the mapping lives in its `UserDbContext`; *called* by
`Mini.UserService`, which exposes the API and holds none of the data.

The External→Local direction is genuinely ambiguous in this sample and answers 409: the
composite string key means the lookup is a suffix match, and carol is `ext-1` at two
providers. The real IdG stores provider and subject in separate columns and never hits this.
_Avoid_: "user mapping" — and don't confuse it with `ExternalUserStore`'s *provisioning*,
which writes the mapping; this only reads it.

**Self-issued JWT**:
A token IdentityServerHost mints for itself via `IIdentityServerTools.IssueClientJwtAsync` —
signed with its own key, no `/connect/token` round trip, no registered client. Carries `iss`,
`nbf`, `iat`, `exp`, `client_id`, `aud` and **nothing else** — in particular no `sub` and no
`scope`.

Confirmed in Phase 11, against a Phase 10 prediction that it would be replaced: it is the real
IdG's actual pattern for reads, so it stays. The absence of `scope` is load-bearing — it's what
lets Mini.UserService's management policies refuse a token that
`ServiceAccountOnlyFilter` alone would accept, since the absent `sub` makes it read as a
service account. See
[`docs/architecture/service-to-service-auth.md`](docs/architecture/service-to-service-auth.md).
_Avoid_: calling it a service-account token — the distinction (revocable vs not) is the whole
reason both exist.

**Connector**:
An integration *type* that can serve an extension point for a tenant — `WebApi`, `AzureGraph` or
`Claim`, one row each in `Mini.UserService`'s `Connectors` table. Ported in Phase 12 from
`DIT.Connectors`. `WebApi` and `Claim` work here; `AzureGraph` is a catalog row whose dispatcher
answers "not implemented in this sample."

The word names a *type*, never a configured instance and never the target system. Acme's own API is a
**connector target** (`Mini.AcmeApi`); the rows that point at it are a **connector configuration**.
_Avoid_: "the connector for acme" — say which handler, since a tenant has one per extension point.
Also don't use it for an external *identity* provider: a `Dynamic provider` is a login source, a
connector is a data source, and neither is involved in the other's job.

**Handler**:
An extension point a connector can serve — a row in `Handlers` plus an `ActionHandlerBase` subclass
that knows how to dispatch it. Two exist: `GetUserRole` (cascading for `acme` and `initech`) and
`GetUserByEmail` (never cascading, which is why it exists — it is the only place a connector failure
is allowed to be fatal).
_Avoid_: "endpoint" — the HTTP route and the handler are different things, and one route can resolve
to a different connector per tenant.

**Catalog / choice / settings**:
The three layers of the connector schema, and the reason it is eight tables rather than one.
*Catalog* (`Connectors`, `Handlers`, `ConnectorHandlers`) says what is possible; *choice*
(`ConnectorHandlerTenants`, `ConnectorHandlerCascadingTenants`) says what a tenant picked; *settings*
(`WebApiConnectorConfigurations` + `…Routes`, `ClaimConnectorConfigurations`) says where to go. Real
`DIT.Connectors` wording, kept verbatim because the split is the design: onboarding a tenant touches
choice and settings only, adding an integration type touches the catalog only.

**Cascading connector chain**:
An ordered list of connectors for one (tenant, handler), tried in `Order` until one answers — the
`ConnectorHandlerCascadingTenants` table, which exists only in the user service's context in the real
library too.

The rule that gives the word meaning, and this sample's own reading rather than a verified port (the
real ordering is documented as living inside the DIT library): **a cascade absorbs failures, a single
connector's failure surfaces.** A chain that runs out reports "no value," not an error.

Its cost is reproduced deliberately: a cascade **silently absorbs misconfiguration**. `initech`'s
`WebApiConnectorConfiguration.Host` names Acme's host, Acme answers 403, the chain moves on, and every
Initech user quietly becomes `"Member"` with HTTP 200. The identical fault on `GetUserByEmail` answers
502, because nothing is configured behind it.
_Avoid_: "fallback" alone — it hides which of the two behaviours is meant, and they differ by exactly
one row.

**Two IsEnabled flags**:
A connector lookup succeeds only when the tenant's own choice row AND the base `ConnectorHandlers`
pairing are both enabled. A kill switch at two levels, so an integration can be withdrawn from
everyone or from one client without deleting anybody's configuration.

`globex` is the live demonstration: it has an *enabled* choice row picking `AzureGraph`, the base
pairing is disabled, so it resolves to nothing and is still served by the `UserIdentityRoles` table.
Same "two indistinguishable causes for a vanished feature" shape as
`DynamicIdentityProviderEnabled` above.

**Mini.AcmeApi**:
Acme Corporation's own user API (`:5014`), added in Phase 12 — the first process in this repo standing
in for a system a **tenant** owns rather than one the platform owns. A `WebApi` connector target,
reached with the per-tenant service-account token (`userservice-svc.acme`) plus an
`OriginUserIdentifier` header.

Deliberately carries none of this repo's conventions (no `IIdentityContext`, no `ProblemDetails`, no
versioned routes, a `Dictionary` for storage) because applying them would imply Acme builds services
the way DIT does. What it does have is an authorization policy on `client_id`: all three
`userservice-svc.{tenant}` clients hold a token it *accepts*, and only Acme's own gets past — a scope
says which resource, never whose data within it.
_Avoid_: calling it a stub or a stand-in for a DIT service. `ExternalServicesStub` stood in for *our*
services; this stands in for a client's.

**KeyManagement:Provider**:
The config value (`"Developer"` or `"AzureKeyVault"`) `SigningKeyExtensions.AddSigningKey`
switches on, ported in Phase 8. `"Developer"` is the default — this sample runs with zero
Azure setup unless you opt in. Not the same three-way choice the real IdG's
`KeyManagementProvider` makes (`None`/`Azure`/`Local`) — `Local` (a cert-file path) isn't
ported here.

**AzureKeyVaultKeyStore**:
One class implementing both `ISigningCredentialStore` and `IValidationKeysStore`,
registered as a single shared singleton for both (not two independent instances) so
there's one `CertificateClient` and one cache entry. Every enabled, non-expired
certificate version in the vault becomes a validation key; only the newest version older
than `RolloverDelayHours` becomes the active signing key — see
`IdentityServerHost/README.md`'s Phase 8 section for why that ordering, not just what it
does.
_Avoid_: assuming this was verified against a real vault in this environment — it wasn't
(see [`docs/azure-key-vault-setup.md`](src/IdentityServerHost/docs/azure-key-vault-setup.md)).
What *was* verified: the dispatcher genuinely activates this store and makes a real
network attempt when configured, rather than silently falling back to the developer key.

**Mini.AuthorizationService**:
Phase 13's authorization decision service (`:5015`), separate from the token. Until now, everything
in this repo — the `role` claim, every permission decision — lived *inside* the JWT token. For decisions
that need to change without re-issuing tokens, or for policies that are too expensive to compute at
every login, or for policies that need context beyond what fits in a claim, a separate service emerges:
the resource server calls back to ask "am I authorized for this?" at request time.

The service accepts bearer tokens and evaluates policies against the caller's identity and context.
Policies are stored per-tenant, in rows (not code), so a policy can be changed in SQL without a restart.
Each policy names a resource, describes who can access it (currently role-based; claim-based is a stub),
and in what order to check if multiple policies apply.

Phase 13 adds the service itself and proves it works (`test-phase13.ps1`). Phase 14 wired it into
SampleApi's `/authorize/{resourceName}` endpoint, called per-request rather than at login. **Phase 21
made it a real publisher**: `PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}` upserts a
`Policy` row's `Condition`, clears the `CachedDecisions` rows that depended on it, and publishes a real
`PolicyChangedEvent` onto the bus — see `PolicyChangedEvent` below. Editing is open to any authenticated
caller on purpose (no role gate), the same "two callers look identical" gap `IIdentityContext` already
documents elsewhere. See [`src/Mini.AuthorizationService/README.md`](src/Mini.AuthorizationService/README.md).

**Agent Portal**:
Phase 17's `src/AgentPortal` — a second server-side MVC client, imitating `Applications.Apply`, with
its own client registration (`agentportal`) on the same IdentityServerHost `MvcClient` logs into. Its
own name is illustrative, not a port of anything named "Agent Portal" in the real IdG or `Applications.Apply`
(confirmed by checking both) — the real precedent for "more than one MVC/BFF client hitting the same
Identity Gateway" is `Applications.Portal`, `Applications.AdminConsole`, and `Applications.CustomerPortal`
sitting alongside `Applications.Apply` in production. Phase 17 was a skeleton: login only, no tenant
resolution, no downstream API call.

Phase 18 gave it a reason to exist: tenant resolution via `Mini.Infrastructure`'s newly-shared
`ITenantContext` (see that entry), and a real call to `Mini.AuthorizationService` for a resource of its
own (`"agent-portal"`, distinct from SampleApi's `"sample-api"`) through the same shared
`AuthorizationClient` Phase 16 extracted. `IIdentityContext` (see below) needed no change to serve a
browser-based caller — it was already claims-only, never assuming a bearer token specifically.
_Avoid_: assuming "Agent Portal" names a real Equisoft product — it doesn't.

**Message bus**:
This sample's port of `Libraries.Infrastructure/DIT.MessageQueue` — a thin MassTransit wrapper, not
a bespoke abstraction. Landed in Phase 19 as `Mini.Infrastructure/Messaging/`: a provider-switching
options class (`MessageQueueProvider`: `InMemory` / `RabbitMQ` / `AzureServiceBus`) and an
`AddMessageBus` extension that binds a `"MessageBus"` config section and branches to MassTransit's
matching `Using*` call. Confirmed by reading the real repo: there is no outbox pattern and no
pre-wired retry/dead-letter handling there either — this sample doesn't invent fidelity the
original library doesn't have.

**Narrower than the design session predicted, on purpose.** The real library's `TenantFilter<T>`
(a publish/consume filter stamping `tenant-key`/`tenant-id` headers) was NOT ported in Phase 19 —
nothing publishes or consumes yet that needs tenant-scoped routing, and building it ahead of a real
consumer would repeat the mistake this repo's own rules warn against (see "Shared concerns go in
Mini.Infrastructure" in the phase skill: port need-driven, not ahead of a consumer). It's deferred to
whichever of Phase 20/21 first needs it. Also narrower: `AzureServiceBus` is a bound options shape
with no MassTransit transport wired behind it (that needs a separate NuGet package this repo doesn't
reference) — same "documented, not exercised" split as `KeyManagement:Provider`'s `AzureKeyVault`
branch (Phase 8). No `IEntityEventHelper`-style wrapper was added either — Phase 19 has no publisher
yet, so there was nothing to wrap; callers inject MassTransit's own `IPublishEndpoint`/`IBusControl`
directly, and a Phase 21 publisher can decide then whether a wrapper earns its keep.
_Avoid_: assuming this is a from-scratch design — it's a port, unlike the webhook pieces below. And
don't assume the tenant filter or an event-helper wrapper exist yet — they don't, see above.

**PolicyChangedEvent**:
The domain event `Mini.AuthorizationService` publishes on the message bus when a `Policy` row is
edited (`TenantKey`, `ResourceName`, old/new `Condition`, `ChangedAtUtc`). Lives in
`Mini.Infrastructure/Messaging/` as of Phase 19 — defined and proven to round-trip over both
MassTransit's in-memory test harness and a real RabbitMQ instance (`test-phase19.ps1`). As of Phase
20, `Mini.MessageCenter` is a real, production consumer of it (`Messaging/PolicyChangedEventConsumer.cs`).
**As of Phase 21, `Mini.AuthorizationService` is a real, production publisher of it too** — its new
`PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}` admin endpoint calls
`IPublishEndpoint.Publish` on every successful write, replacing Mini.MessageCenter's throwaway
diagnostic endpoint (`POST /api/v1/test/publish-policy-changed`, removed in Phase 21 exactly as it
said it would be) as the real trigger. The `PolicyChangedEventConsumer` in
`tests/StepwiseIdentity.Tests` still exists too, unchanged, purely to prove the wire format works
without needing a running service. Deliberately a named, domain-specific event rather than the real
library's generic `EntityUpdatedEvent` envelope — chosen so `Mini.MessageCenter` doesn't need to know
`EntityType == "Policy"` means something.
_Avoid_: confusing with `CachedDecision`, which is an authorization *answer*, not a change
notification. And don't assume this is the only way a `Policy` row could ever change — nothing
outside `Mini.AuthorizationService` writes to `Policies` directly (see `PolicyChangeRequest` below
for why Phase 22's Agent Portal doesn't get to either).

**Mini.MessageCenter**:
Phase 20's service (`:5017`) that consumes `PolicyChangedEvent` off the real RabbitMQ bus and fans
it out to webhook subscribers. Its name follows this repo's `Mini.X` convention (like
`Mini.UserService`, `Mini.AuthorizationService`), even though the real service it's loosely inspired
by is named `Services.MessageCenter` in production. The first real, cross-process consumer of Phase
19's `AddMessageBus` — until this phase, only the same-process test harness and `test-phase19.ps1`
had ever used it.

**Its webhook system has no real counterpart to port.** Both `Libraries.Infrastructure` and the real
`Services.MessageCenter` were checked directly: neither has a subscription model, HMAC signing, or
delivery-retry logic. The real `Services.MessageCenter` calls `Services.Notifications` via a plain,
synchronous, unsigned, fire-once HTTP POST — nothing like a webhook fan-out. So `Mini.MessageCenter`'s
webhook design is this course's own invention, not a port. See
[`src/Mini.MessageCenter/README.md`](src/Mini.MessageCenter/README.md) and
[`docs/architecture/webhooks.md`](docs/architecture/webhooks.md).

**Its Phase 20 diagnostic endpoint is gone.** Through Phase 20, `Mini.AuthorizationService` didn't
publish `PolicyChangedEvent` yet, so Phase 20 proved the consumer and delivery path with a throwaway
endpoint, `POST /api/v1/test/publish-policy-changed`, documented as removable once Phase 21 shipped a
real publisher. Phase 21 removed it — `Mini.AuthorizationService`'s policy-admin API is now the only
thing that triggers a `PolicyChangedEvent`. `test-phase20.ps1` is kept (per this repo's
never-delete-a-superseded-artifact rule) but can no longer run past the point where it called that
endpoint; `test-phase21.ps1` exercises the real path instead.
_Avoid_: calling it "a port of Services.MessageCenter" unqualified — only the *name* and the general
shape ("a service downstream services notify") come from there; the webhook mechanism is new. And
don't assume the diagnostic endpoint still exists — see above.

**Webhook subscription**:
A seeded row in `Mini.MessageCenter`'s own database naming a receiver: callback URL, a shared HMAC
secret, and a nullable `TenantKey` scope (`null` = unscoped, every tenant's events). Two are seeded:
`Mini.AcmeApi`'s (`:5014`, scoped to `acme`) and a new `WebhookReceiverStub` (`:5018`, unscoped) —
mirroring the "which tenant does this route to" lesson `Connector` already teaches, via
`WebhookSubscriptionMatcher.Matches`, table-tested in
`tests/StepwiseIdentity.Tests/WebhookSubscriptionMatcherTests.cs`. Seeded as rows, the same way
`Mini.AuthorizationService`'s own policies were seeded before any admin API existed for them — a
subscription-management API is explicitly future work, not part of this phase.
_Avoid_: "webhook endpoint" alone — say subscription when meaning the stored row, delivery when
meaning one outbound attempt (a `DeliveryAttempt` row in `Mini.MessageCenter`'s database).

**WebhookReceiverStub**:
Phase 20's minimal testing aid (`:5018`), in the same spirit as `ExternalServicesStub`: a single
`POST /webhook` endpoint that verifies a delivery's HMAC-SHA256 signature and logs it, plus
`GET /webhook/received` for a test script to poll. `Mini.MessageCenter`'s unscoped subscriber — the
only receiver in this phase that actually checks a signature (`Mini.AcmeApi`'s webhook endpoint
does not; see its README's Phase 20 addition for why that's a documented gap, not an oversight).

**PolicyChangeRequest** (planned):
An audit-trail row in Agent Portal's own new database: who (the signed-in agent's `sub`) changed
which policy, the before/after `Condition`, and when. Exists so Agent Portal has a genuine reason
to own a database, without duplicating who owns the `Policy` concept itself — the canonical row
stays in `Mini.AuthorizationService`; this is a log of *that a change happened*, not a second copy
of the policy.
_Avoid_: treating this as the source of truth for a policy's current state — it never is.

**CachedDecision**:
Phase 15's persisted authorization-decision cache — a row in `AuthorizationDbContext`, keyed by
(tenant, caller, resource, a hash of the evaluation context), holding the last decision and an
expiry. `POST /evaluate` reads it first and only re-runs `Policy.EvaluatePolicy` on a miss or an
expired row. The distinction that matters: this is *not* an in-memory `Dictionary` — it lives in the
same SQL Server database as `Policy`, so it survives a restart of Mini.AuthorizationService, unlike
a naive process-local cache. `DELETE /api/v1/authorization/cache/{tenantKey}` clears it early
(service accounts only); SampleApi's `DELETE /admin/cache/{tenantKey}` — a no-op simulation through
Phase 14 — now forwards to that endpoint.
