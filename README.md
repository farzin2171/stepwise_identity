# stepwise_identity
this is a repo to explain how identity works in our microservices environment

New to OAuth 2.0 / OpenID Connect itself, independent of anything in this repo? Start
with [`docs/reference/`](docs/reference/README.md) — a vendor-neutral reference on the
protocol concepts, cross-linked into the phase-by-phase code below wherever they show
up.

## Mini IdG

A mini Identity Gateway, built from scratch in phases that mirror
`Applications.IdentityGateway`'s real architecture:

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ✓
4. External identity providers ✓
5. Persistence (SQL Server instead of in-memory) ✓
6. Data ingestion / config tooling ✓
7. DIT external-service calls (TenantClient, UserClient) ✓
8. Signing-key management (Key Vault instead of a developer credential) ✓
9. IdentityProviderStore (DB-persisted external-provider config) ✓
10. Mini.Infrastructure (extract the genuinely duplicated plumbing) ✓
11. Mini.UserService (a real service replaces ExternalServicesStub) ✓
12. Connectors (per-tenant, cascading user sources) ✓
13. Mini.AuthorizationService (the permission decision leaves the token) ✓
14. (authorization integration — SampleApi calls the authorization service) ✓
15. (persist authorization decisions across restarts) ← next
```

- [src/IdentityServerHost](src/IdentityServerHost) — the authorization server. See its
  [README](src/IdentityServerHost/README.md) for what each phase adds and why.
- [src/MvcClient](src/MvcClient) — a server-side (confidential) MVC app that logs in
  against it. See its [README](src/MvcClient/README.md).
- [src/ReactSpa](src/ReactSpa) — a browser-based (public) SPA that logs in against the
  same server with a different client configuration, because it can't keep a secret.
  See its [README](src/ReactSpa/README.md).
- [src/SampleApi](src/SampleApi) — a JWT-Bearer-protected API that both MvcClient and
  ReactSpa call on the signed-in user's behalf, using the access token from login. See
  its [README](src/SampleApi/README.md).
- [src/ExternalIdp](src/ExternalIdp) — a second, independent Duende IdentityServer that
  IdentityServerHost federates to (Acme's users only) as of Phase 4. See its
  [README](src/ExternalIdp/README.md).
- [src/Tools/ConfigIngestionTool](src/Tools/ConfigIngestionTool) — Phase 6's data-ingestion
  tool: reads `IdentityServerHost/Configurations/IdentityServerConfig.json` and writes it
  into the same SQL Server database IdentityServerHost reads from. See its
  [README](src/Tools/ConfigIngestionTool/README.md).
- [src/Mini.UserService](src/Mini.UserService) — Phase 11's real service standing in for two
  sibling DIT microservices (a Tenant Management API and a User API) that IdentityServerHost
  calls at token-issuance time. Its own `MiniUsers` database, a service-account-gated
  management API, and a call *back* into IdentityServerHost — the dependency is bidirectional,
  as it is in production. Phase 12 added the connector machinery that decides, per tenant, where a
  user's data comes from. See its [README](src/Mini.UserService/README.md).
- [src/Mini.AcmeApi](src/Mini.AcmeApi) — Phase 12's stand-in for a system a **tenant** owns rather
  than one the platform owns: Acme Corporation's own user API, reached by a WebApi *connector* whose
  host and routes are rows in SQL. The first process here that isn't ours. See its
  [README](src/Mini.AcmeApi/README.md).
- [src/Mini.AuthorizationService](src/Mini.AuthorizationService) — Phase 13's authorization decision
  service: where authorization policies live when they can't be embedded in a token (because they
  need to change without re-issuing tokens, or because they need runtime context). Called by SampleApi
  to evaluate policies per tenant. See its [README](src/Mini.AuthorizationService/README.md).
- [src/ExternalServicesStub](src/ExternalServicesStub) — Phase 7's hardcoded-dictionary
  version of the same thing, **superseded** in Phase 11 and kept for comparison, not deleted:
  the value of a phase course is the diff between phases. `.\run-all.ps1 -IncludeStub` starts
  both. See its [README](src/ExternalServicesStub/README.md).
- [src/Mini.Infrastructure](src/Mini.Infrastructure) — Phase 10's shared plumbing, created
  by *extracting* what nine phases of building one project at a time had duplicated. Its
  [README](src/Mini.Infrastructure/README.md) is worth reading for what it deliberately
  does **not** contain: the two `TenantContext`s and the two tenant registries stay
  separate, because they turned out to be different concepts wearing the same names.
- [tests/StepwiseIdentity.Tests](tests/StepwiseIdentity.Tests) — the repo's single xunit
  project, added in Phase 11 when the first genuinely branching logic arrived. Decision
  tables only; everything else is verified end to end by a `test-phase*.ps1`.

**Start everything with [`run-all.ps1`](run-all.ps1)** (Phase 10) — one command instead of
five terminals, running the config-ingestion step first and waiting for each `/health`
endpoint. `.\run-all.ps1 -Stop` shuts it all down.

There's a current-state map of the whole system in
[docs/architecture/](docs/architecture/README.md): who runs on which port, the four ways a
token moves, where state lives, and the three tenant registries that agree only by
convention. Unlike the phase-by-phase READMEs, it describes the system as it is *now*. It now
also carries
[service-to-service-auth.md](docs/architecture/service-to-service-auth.md) — the three ways a
process proves who it is when there's no user involved, and which one can't be revoked — and
[connectors.md](docs/architecture/connectors.md), on where a tenant's user data comes from when
that's decided by rows instead of code, including the finding that a cascading fallback chain
silently absorbs a broken integration and quietly downgrades a `role` claim while doing it.

External providers are now config-driven — a first step toward how
`Applications.IdentityGateway` actually does it, ported into
[src/IdentityServerHost/Configurations/Authentication](src/IdentityServerHost/Configurations/Authentication):
- [src/IdentityServerHost/docs/external-providers-configuration.md](src/IdentityServerHost/docs/external-providers-configuration.md)
  — the `ExternalProviders` config shape, the settings reference, how tenant gating
  works now, and what's modeled but not yet wired (`FederatedConfiguration`,
  `ClaimMappings`).
- [src/IdentityServerHost/docs/azure-entra-b2c-setup.md](src/IdentityServerHost/docs/azure-entra-b2c-setup.md)
  — step-by-step setup for a real Microsoft Entra ID tenant or Azure AD B2C tenant,
  updated for the config-driven wiring above.

IdentityServerHost's signing key is config-driven too, as of Phase 8 — swap
`AddDeveloperSigningCredential()` for a real Azure Key Vault-backed one via
`KeyManagement:Provider`, ported into
[src/IdentityServerHost/KeyManagement](src/IdentityServerHost/KeyManagement):
- [src/IdentityServerHost/docs/azure-key-vault-setup.md](src/IdentityServerHost/docs/azure-key-vault-setup.md)
  — creating a real vault and signing certificate, the RBAC role (and the
  certificates-vs-secrets gotcha) the app actually needs, authenticating with either
  your own `az login` session or a service principal, and verifying and rotating a real
  key end to end.

As of Phase 9, external providers no longer have to come from `appsettings.json` at all —
IdentityServerHost ports the real IdG's custom `IdentityProviderStore`, so a provider can
be a row in the `IdentityProviders` table resolved at request time (Duende calls these
*dynamic providers*), reaching the same `IAuthenticationOptions` interface the file-based
ones already implement:
[src/IdentityServerHost/IdentityServer](src/IdentityServerHost/IdentityServer) — the store
and its provider models, with the `Phase 9` section of
[IdentityServerHost's README](src/IdentityServerHost/README.md) covering why tenant
filtering here is a *different design* from the real IdG's rather than a smaller one, and
seven things that broke on the way (including a captive-dependency crash and a stale
database row that nearly broke Phase 4's test).

MvcClient also now carries a port of `Applications.Apply`'s (the real production MVC
BFF) multi-tenancy infrastructure and its `IdentityGatewayApi`/`ExternalServicesApi`
integration patterns:
[src/MvcClient/docs/multitenancy-and-external-services.md](src/MvcClient/docs/multitenancy-and-external-services.md)
— `ITenantContext`, tenant-aware login redirects, a per-tenant service-account token
client, and a config-driven external-service registry with Polly retry/circuit-breaker
resilience, each section compared against the real Apply code it was ported from.

SampleApi now carries a port of `Services.Authorization`'s (the real production DIT
authorization-decision service) identity/claims plumbing and API conventions:
[src/SampleApi/docs/identity-context-and-conventions.md](src/SampleApi/docs/identity-context-and-conventions.md)
— `IIdentityContext` (claims-only multi-tenancy for a caller with no browser at all),
route versioning (`/api/v1/identity`), `ProblemDetails`, and a service-account-only
endpoint filter, each section compared against the real `Services.Authorization` code
it was ported from.

Verification scripts (repo root):

- [`test-phase2.ps1`](test-phase2.ps1) — the MvcClient login flow end-to-end, no
  browser needed.
- [`test-api.ps1`](test-api.ps1) — the same login, then *Call the API* from MvcClient.
- [`test-phase2-spa.ps1`](test-phase2-spa.ps1) — proves ReactSpa's IdentityServer-side
  login config (public client, no secret, CORS on `/connect/token`) is correct.
- [`test-spa-api.ps1`](test-spa-api.ps1) — proves the same for ReactSpa's own *Call the
  API* button (the `api1` scope, and SampleApi's CORS policy for the browser origin).
- [`test-phase3.ps1`](test-phase3.ps1) — proves tenant resolution: matching
  tenant/user succeeds with the right `tenant_id` claim, a mismatched tenant is
  rejected, and a login with no tenant hint at all still works.
- [`test-phase4.ps1`](test-phase4.ps1) — proves per-tenant external IdP federation:
  Globex sees no external sign-in option, Acme does and can complete a real federated
  login through ExternalIdp (a separate server), ending with `name` from ExternalIdp and
  `tenant_id` from the original request.
- [`test-multitenancy-external-services.ps1`](test-multitenancy-external-services.ps1) —
  proves MvcClient's `ITenantContext` resolves from the `tenant_id` claim, and that
  calling SampleApi with the forwarded user token vs. a service-account token
  (`mvcclient-svc.acme`/`mvcclient-svc.globex`) produces meaningfully different claims.
- [`test-sampleapi-identity-context.ps1`](test-sampleapi-identity-context.ps1) — proves
  SampleApi's `IIdentityContext` resolves `IdentityType`/`TenantKey` differently for a
  user token (the `tenant_id` claim) vs. a service-account token (parsed from the
  `client_id` suffix instead), and that `ServiceAccountOnlyFilter` on
  `DELETE /api/v1/admin/cache/{tenantKey}` really does discriminate by identity type
  (401 with no token, 403 for a real user, 200 for a service account).
- [`test-phase5.ps1`](test-phase5.ps1) — proves Clients/Resources/grants and
  federated-login provisioning are SQL Server-backed now, by querying LocalDB directly.
- [`test-phase6.ps1`](test-phase6.ps1) — proves `Configurations/IdentityServerConfig.json`
  is authoritative: corrupts a client directly in the database, re-runs
  `ConfigIngestionTool`, and confirms both the row and a real login are restored.
- [`test-phase7.ps1`](test-phase7.ps1) — proves `tenant_guid`/`role` resolve from the
  external service via IdentityServerHost's own self-issued-JWT calls, and reach
  both IdentityServerHost's and SampleApi's tokens. **Unmodified since Phase 7, and now
  the regression test for Phase 11** — it was written against `ExternalServicesStub` and
  exercises `Mini.UserService` instead, which is what makes "replacement" a claim rather
  than a hope.
- [`test-phase8.ps1`](test-phase8.ps1) — confirms the default developer signing key
  still works after adding the Key Vault code path, then prints manual steps for
  proving the `AzureKeyVault` provider is really wired up (see
  [src/IdentityServerHost/docs/azure-key-vault-setup.md](src/IdentityServerHost/docs/azure-key-vault-setup.md)
  for using a real vault).
- [`test-phase9.ps1`](test-phase9.ps1) — proves an external provider that exists *only*
  as a database row becomes a login button for its tenant (`initech`) and can complete a
  real federated login through Duende's dynamic-provider path
  (`/federation/{scheme}/signin`), while `acme` (file-based) and `globex` (none) are
  unaffected.
- [`test-phase10.ps1`](test-phase10.ps1) — proves the Phase 10 extraction changed nothing
  observable: five `/health` endpoints answer, `IIdentityContext` still tells a user from a
  service account, and `ServiceAccountOnlyFilter` still answers 200/403/401. The real
  regression suite for that phase is every script above it, run unmodified.
- [`test-phase11.ps1`](test-phase11.ps1) — proves `Mini.UserService` replaced the stub without
  changing behaviour, in eight parts: the registry now coming from SQL (with the GUID's *case*
  pinned, after a real bug), the two collapsed services keeping separate audiences, a tenant
  onboarded over HTTP with no restart, management refusing user and wrong-audience callers, the
  bidirectional call back into IdentityServerHost, and a genuinely ambiguous id conversion
  answering 409.
- [`test-phase12.ps1`](test-phase12.ps1) — proves a tenant's user source is decided by *rows*, in
  eight parts: Acme's own API refusing anonymous, wrong-audience and wrong-**tenant** callers; acme's
  cascade answering out of Acme's HR system (with carol, who has no row anywhere here, as the proof);
  the chain's *ordering* shown by one token being answered at position 2 and shadowed at position 1;
  globex resolving to no connector at all despite an enabled choice row, because a lookup needs both
  `IsEnabled` flags; the same misconfiguration silently absorbed by a cascade and surfaced as a 502
  without one; and a real login carrying a `role` claim out of a system this repo doesn't own.
- [`test-phase13.ps1`](test-phase13.ps1) — proves `Mini.AuthorizationService` exists and can evaluate
  policies, in seven parts: the service answers `/health`, requires auth for policy endpoints, service
  accounts can get authapi tokens from IdentityServerHost, the service lists policies per tenant,
  policies are evaluated correctly (Admin allowed, Member denied for Acme), and per-tenant policies work
  (Globex allows Member where Acme doesn't). The service exists but is not yet called on any login path.
- [`test-phase14.ps1`](test-phase14.ps1) — proves SampleApi is integrated with Mini.AuthorizationService,
  in six parts: the new `/authorize/{resourceName}` endpoint requires authentication, both Acme and Globex
  users can log in (regression test), the authorization service is called and returns per-tenant decisions,
  the `/identity` endpoint still works (regression), and service accounts can call the authorize endpoint.
  Authorization decisions now come from the service, not just the token.

Plus one xunit project, [`tests/StepwiseIdentity.Tests`](tests/StepwiseIdentity.Tests)
(`dotnet test`), added in Phase 11 for the decision tables a black-box HTTP script would
document badly: identity conversion, the identity-type rules every service now depends on,
and — since Phase 12 — which connector serves a given tenant and what a cascading chain's
outcome is. The connector-resolution tests run against the **shipped** seed rows, so they pin
the decision table itself rather than only the code that reads it.

None of the scripts drive real browser JavaScript — see
[src/ReactSpa/README.md](src/ReactSpa/README.md) for why an actual click-through in a
browser is still worth doing at least once for both client apps.

As of Phase 10, `run-all.ps1` does all of the setup below for you — the paragraph is kept
because knowing what it does is the point.

All relevant apps for a given script must already be running (`dotnet run` /
`npm run dev`, per project README) before you run it. As of Phase 6, IdentityServerHost's
database also needs `src/Tools/ConfigIngestionTool` run at least once first — see its
README — since IdentityServerHost itself no longer seeds any Clients/Resources on
startup. As of Phase 11, `src/Mini.UserService` must also be running for any login to
succeed (IdentityServerHost calls it during token issuance — it was
`src/ExternalServicesStub` from Phase 7 until then), and it creates its own separate
`MiniUsers` database on first run. As of Phase 12, `src/Mini.AcmeApi` must be running too,
or Acme users lose their `role` claim — the login still succeeds, silently, with `Member`
instead. `run-all.ps1` handles all of this ordering.
