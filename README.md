# stepwise_identity
this is a repo to explain how identity works in our microservices environment

## Mini IdG

A mini Identity Gateway, built from scratch in phases that mirror
`Applications.IdentityGateway`'s real architecture:

```
1. Foundation ✓
2. Clients ✓ (MVC + React)
3. Multi-tenancy ← next
4. External identity providers
5. Persistence (SQL Server instead of in-memory)
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

Verification scripts (repo root):

- [`test-phase2.ps1`](test-phase2.ps1) — the MvcClient login flow end-to-end, no
  browser needed.
- [`test-api.ps1`](test-api.ps1) — the same login, then *Call the API* from MvcClient.
- [`test-phase2-spa.ps1`](test-phase2-spa.ps1) — proves ReactSpa's IdentityServer-side
  login config (public client, no secret, CORS on `/connect/token`) is correct.
- [`test-spa-api.ps1`](test-spa-api.ps1) — proves the same for ReactSpa's own *Call the
  API* button (the `api1` scope, and SampleApi's CORS policy for the browser origin).

None of the four drive real browser JavaScript — see
[src/ReactSpa/README.md](src/ReactSpa/README.md) for why an actual click-through in a
browser is still worth doing at least once for both client apps.

All relevant apps for a given script must already be running (`dotnet run` /
`npm run dev`, per project README) before you run it.
