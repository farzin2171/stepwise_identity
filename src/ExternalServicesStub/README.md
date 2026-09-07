# ExternalServicesStub — a stand-in for two real DIT microservices

> **Superseded in Phase 11 by [`Mini.UserService`](../Mini.UserService) (`:5013`).** Nothing calls this
> any more: `IdentityServerHost`'s `ExternalServicesApi` config points at `:5013`, and `run-all.ps1`
> no longer starts this project unless you pass `-IncludeStub`.
>
> **It is kept, not deleted**, and the reason is the point of a phase course: the value is the *diff*
> between phases, and deleting the predecessor retroactively erases what Phase 7 taught. This is 60
> lines with two `Dictionary` literals; its replacement is a service with a database, migrations, an
> authenticated management API and an outbound call of its own. Reading them side by side is the
> shortest available answer to "what does 'make it a real service' actually cost?"
>
> Start both and compare:
>
> ```powershell
> .\run-all.ps1 -IncludeStub     # stub on :5012, Mini.UserService on :5013
> ```
>
> Everything below describes this project as Phase 7 built it, and is left unchanged.

The real IdG calls out to two sibling DIT microservices at token-issuance time — a
Tenant Management API (`TenantClient.GetTenantAsync`) and a User API
(`UserClient.GetRoleAsync`) — rather than owning that data itself. This project is a
minimal stand-in for both, collapsed into one process purely for this course's sake
(the real system has two separate services). See
[`../IdentityServerHost/README.md`](../IdentityServerHost/README.md)'s Phase 7 section
for the full write-up of what calls this and why, including a real bug this course
reproduces on purpose.

## What's in this project

Two endpoints, each backed by a hardcoded dictionary instead of a real database:

- `GET /api/v1/tenants/GetByKey/{key}` → `{ "tenantId": "<guid>" }` for `acme`/`globex`,
  `404` otherwise.
- `GET /api/v2/User/identities/role/{subjectId}` → a plain-text role name, `"Member"` if
  the subject id isn't in the table.

Both require a Bearer JWT, validated against **IdentityServerHost's own** discovery
document (`Authority = https://localhost:5001`) — IdentityServerHost mints these tokens
itself (`IIdentityServerTools.IssueClientJwtAsync`), acting as its own OAuth client, so
there's nothing extra to configure here beyond trusting the same issuer every other
token in this sample already comes from.

## Running it

This project doesn't do anything on its own — it just needs to be up when
IdentityServerHost tries to call it. See
[`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it) for the
full multi-terminal setup.

```bash
cd src/ExternalServicesStub
dotnet run
```

## What's deliberately missing (and why)

- **A real database.** Both endpoints are in-memory dictionaries, reset on every
  restart — fine, since nothing here is meant to be provisioned at runtime the way
  `ExternalUserStore` is.
- **Two separate services.** The real IdG's Tenant Management API and User API are
  independent deployments with independent audiences; this sample collapses them
  because standing up two near-identical stub projects would teach nothing a comment
  couldn't.
- **Any resilience or rate limiting of its own.** It's the target of `TenantClient`'s
  and `UserClient`'s Polly retry/circuit-breaker policies, not a system that needs its
  own.
