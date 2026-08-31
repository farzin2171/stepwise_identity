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
9. IdentityProviderStore (DB-persisted external-provider config) ← next
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
- [src/ExternalServicesStub](src/ExternalServicesStub) — Phase 7's stand-in for two real
  DIT microservices (a Tenant Management API and a User API) that IdentityServerHost
  calls at token-issuance time. See its [README](src/ExternalServicesStub/README.md).

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
- [`test-phase7.ps1`](test-phase7.ps1) — proves `tenant_guid`/`role` resolve from
  `ExternalServicesStub` via IdentityServerHost's own self-issued-JWT calls, and reach
  both IdentityServerHost's and SampleApi's tokens.
- [`test-phase8.ps1`](test-phase8.ps1) — confirms the default developer signing key
  still works after adding the Key Vault code path, then prints manual steps for
  proving the `AzureKeyVault` provider is really wired up (see
  [src/IdentityServerHost/docs/azure-key-vault-setup.md](src/IdentityServerHost/docs/azure-key-vault-setup.md)
  for using a real vault).

None of the twelve drive real browser JavaScript — see
[src/ReactSpa/README.md](src/ReactSpa/README.md) for why an actual click-through in a
browser is still worth doing at least once for both client apps.

All relevant apps for a given script must already be running (`dotnet run` /
`npm run dev`, per project README) before you run it. As of Phase 6, IdentityServerHost's
database also needs `src/Tools/ConfigIngestionTool` run at least once first — see its
README — since IdentityServerHost itself no longer seeds any Clients/Resources on
startup. As of Phase 7, `src/ExternalServicesStub` must also be running for any login to
succeed (IdentityServerHost calls it during token issuance).
