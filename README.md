# stepwise_identity
this is a repo to explain how identity works in our microservices environment

## Mini IdG

A mini Identity Gateway, built from scratch in phases that mirror
`Applications.IdentityGateway`'s real architecture:

```
1. Foundation ✓
2. Clients (MVC ✓, React next)
3. Multi-tenancy
4. External identity providers
5. Persistence (SQL Server instead of in-memory)
6. Data ingestion / config tooling
```

- [src/IdentityServerHost](src/IdentityServerHost) — the authorization server. See its
  [README](src/IdentityServerHost/README.md) for what each phase adds and why.
- [src/MvcClient](src/MvcClient) — a server-side MVC app that logs in against it (added
  in Phase 2). See its [README](src/MvcClient/README.md).
- [src/SampleApi](src/SampleApi) — a JWT-Bearer-protected API that MvcClient calls on
  the signed-in user's behalf, using the access token from login. See its
  [README](src/SampleApi/README.md).

Run [`test-phase2.ps1`](test-phase2.ps1) to verify the login flow end-to-end without a
browser, and [`test-api.ps1`](test-api.ps1) to verify the same login plus the API call
(all three apps must be running first).
