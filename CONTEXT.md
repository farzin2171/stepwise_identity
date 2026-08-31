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

**TenantClient** / **UserClient** (IdentityServerHost):
Ported in Phase 7. HTTP clients to `ExternalServicesStub`, authenticated with a
self-issued JWT (`IIdentityServerTools.IssueClientJwtAsync`) rather than a registered
OAuth client. `TenantClient.GetTenantAsync` resolves a tenant *key* ("acme") to its
`tenant_guid` claim, cached forever on purpose (the real system's own bug, reproduced
here). `UserClient.GetRoleAsync` resolves a `role` claim, never cached — the deliberate
contrast. Both called from `SampleProfileService`, not a separate component — see
`ExternalServicesStub` below.
_Avoid_: confusing this with `Tenants.cs` — that resolves *which* tenant a login is for
(from `acr_values`); `TenantClient` only resolves *that* tenant's GUID, a separate,
downstream, additive step.

**tenant_guid**:
The claim `TenantClient` adds, holding the GUID `ExternalServicesStub` resolved from the
`tenant_id` claim's key. Additive, not a replacement — `tenant_id` still holds the
friendly key (Phase 3's shape), since MvcClient/SampleApi's tenant resolution both
already depend on that shape.

**role**:
The claim `UserClient` adds, holding whatever `ExternalServicesStub` returns for a
subject id — never cached. Exists purely to contrast with `tenant_guid`'s cached (and
deliberately broken) lookup; not a real permissions/roles system.

**ExternalServicesStub**:
The stand-in for the real IdG's two sibling DIT microservices (Tenant Management API,
User API), collapsed into one process. Validates the self-issued JWTs
`TenantClient`/`UserClient` send by trusting IdentityServerHost's own signing key —
nothing else to configure.
_Avoid_: confusing with `ExternalIdp` — that's a stand-in for an external *identity*
provider (a login source); this is a stand-in for backend *data* services IdentityServerHost
calls at token-issuance time, never involved in authentication itself.

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
