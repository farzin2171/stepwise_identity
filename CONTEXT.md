# stepwise_identity

A from-scratch teaching port of `Applications.IdentityGateway`, built one phase at a
time — this file is the glossary for this sample's *own* invented vocabulary. Standard
OIDC/Duende terms (client, scope, resource, grant type, subject) are documented well
enough elsewhere and are deliberately not repeated here.

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

**ITenantContext** (MvcClient):
MvcClient's own tenant abstraction, ported from `Applications.Apply` — resolves from the
`tenant_id` *claim* on the already-signed-in user, not a query parameter. A different
mechanism for a related concept; the two `TenantContext`s don't share code or a type.

**Tenants.cs**:
The hardcoded tenant-key → GUID/display-name dictionary standing in for the real IdG's
`TenantClient` HTTP lookup. Deliberately has no cache — the reason the real system's
never-expiring-cache bug (see `TenantClient` below) can't reproduce in this sample.

**ExternalUserStore**:
The first-login provisioning store for federated identities: persists what a federated
login provisioned (`name`, `tenant_id`) so `SampleProfileService` can look it up at
token-issuance time, since IdentityServer's own `context.Subject` doesn't carry it.
SQL-backed (via `UserDbContext`) as of Phase 5.
_Avoid_: "user store" alone — ambiguous with `TestUserStore`, Duende's own local-password
store.

**EcosystemTenant**:
The config field on each entry under the `ExternalProviders` section naming which tenant
that external provider belongs to. The real IdG resolves this from the *scheme name*
instead — a real difference in shape, not just a simplification.
_Avoid_: "tenant" alone.

**Service account**:
A client-credentials-grant client (e.g. `mvcclient-svc.acme`), one per tenant, for
server-to-server calls with no user or browser involved — ported from `Apply`'s pattern.
_Avoid_: "service client," "machine user."

**IIdentityContext / IdentityType**:
SampleApi's claims-only abstraction over "who is calling" — a `User` identity (has a
`tenant_id` claim) vs. a `ServiceAccount` identity (tenant parsed from the `client_id`
suffix instead) — ported from `Services.Authorization`.
_Avoid_: "caller," "principal" alone.

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

**TenantClient** / **UserClient** (real IdG, not yet ported):
The real IdG's own HTTP clients to sibling DIT microservices — `TenantClient.GetTenantAsync`
(Tenant Management service, Redis-cached forever — the never-expiring-cache bug) and
`UserClient.GetRoleAsync` (User service, never cached). Planned for Phase 7; named here
because Phase 3's `Tenants.cs` already exists specifically *because* this isn't ported
yet.
