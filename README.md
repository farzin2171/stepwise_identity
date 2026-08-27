# stepwise_identity
this is a repo to explain how identity works in our microservices environment

## Mini IdG

A mini Identity Gateway, built from scratch in phases that mirror
`Applications.IdentityGateway`'s real architecture:

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ✓
4. External identity providers ✓
5. Persistence (SQL Server instead of in-memory) ← next
6. Data ingestion / config tooling
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

None of the six drive real browser JavaScript — see
[src/ReactSpa/README.md](src/ReactSpa/README.md) for why an actual click-through in a
browser is still worth doing at least once for both client apps.

All relevant apps for a given script must already be running (`dotnet run` /
`npm run dev`, per project README) before you run it.
