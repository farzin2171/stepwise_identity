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

Run [`test-phase2.ps1`](test-phase2.ps1) to verify the current Authorization Code + PKCE
flow end-to-end without a browser (both apps must be running first).
