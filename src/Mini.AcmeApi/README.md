# Mini.AcmeApi

Acme Corporation's **own** user API. Runs on **`https://localhost:5014`**, added in Phase 12.

This is the first process in this repo that stands in for a system a **tenant** owns rather than one
the platform owns. Everything else here — `IdentityServerHost`, `Mini.UserService`, `SampleApi`, even
`ExternalIdp` — is infrastructure the DIT side runs. This is not. Read it as somebody else's code that
happens to live in the same solution for convenience.

It has no idea what a tenant is. No `Tenants` table, no `tenant_id` claim handling, no notion of the
[three tenant registries](../../docs/architecture/README.md#tenancy-three-registries-that-agree-only-by-convention).
It knows two employees, because Acme's HR system knows two employees.

## What it stands in for

Real counterpart: the "custom web APIs" half of `Services.User`'s per-tenant connector chain. From the
`dit-architecture` reference for that service:

> **Custom web APIs** — `IWebApiConnector`: per-tenant/per-handler endpoints and parameterized routes;
> bearer auth via a service-account token; can inject an `OriginUserIdentifier` header for user
> context.

There is no single real repo this ports — that is the point. In production it is *whatever a client
runs*: an HR system, an in-house directory, a legacy policy admin platform. What is portable is the
**contract** the connector expects: an HTTP GET at a configured host and route, a bearer token, and a
404 when the user isn't there.

## The two routes

```
GET /users/{userId}                 -> 200 { userId, displayName, email, role, originUserIdentifier }
                                       404 if Acme's directory has never heard of them
GET /users/by-email/{email}         -> 200 { userId, displayName, email, role }
                                       404 otherwise
GET /health                         -> 200, anonymous
```

Neither route shape lives in any C# file in this repo. They live in
`WebApiConnectorConfigurationRoutes` rows (`users/{id}`, `users/by-email/{email}`) in the `MiniUsers`
database, which is why "Acme renamed a route" is a SQL update rather than a deployment. See
[`docs/architecture/connectors.md`](../../docs/architecture/connectors.md).

## Acme's directory

| userId | Who | Role | Why this row exists |
| --- | --- | --- | --- |
| `1` | Alice Anderson | `Admin` | **Byte-identical to what `MiniUsers`' `UserIdentityRoles` says about her**, deliberately, so [`test-phase7.ps1`](../../test-phase7.ps1) passes unmodified while the *source* of her `role` claim moves. Same trick Phase 11 played with the tenant GUIDs. |
| `external:external-idp:ext-1` | Carol Carter | `Underwriter` | **The proof.** Carol is a federated identity with no row anywhere in `MiniUsers`, so before Phase 12 she got the `Member` fallback. `Underwriter` exists nowhere in this repo's databases — only here. |

Note carol's id: `external:{scheme}:{externalSubjectId}`, the composite local subject id from Phase 5.
It arrives in the URL as `users/external%3Aexternal-idp%3Aext-1`, which is the Phase 5 shortcut
showing up as somebody else's URL-encoding problem.

## Authentication: two different checks, doing two different jobs

Acme trusts the DIT platform's **issuer** — same discovery document, same JWKS as `SampleApi` and
`Mini.UserService`. That's what makes it reachable as a connector target without a separate credential
exchange. But trusting the issuer is not the same as trusting every tenant on it:

```
   scope "acmeapi"   ──▶  which RESOURCE may be reached   ──▶  audience check   ──▶  401 if wrong
   client_id         ──▶  whose DATA within it            ──▶  policy check     ──▶  403 if wrong
```

All three `userservice-svc.{acme,globex,initech}` clients are allowed the `acmeapi` scope, so **all
three hold a token Acme accepts**. The `AcmeServiceAccount` policy in `Program.cs` requires
`client_id == userservice-svc.acme`, and only Acme's own gets through.

That is not defensive decoration. Initech's `WebApiConnectorConfiguration.Host` deliberately names
*this* host — the single most likely connector misconfiguration there is — and this policy is the only
thing in the entire chain positioned to catch it. A scope cannot express "this tenant's data." Without
the policy, Initech's mis-pointed row would have quietly served Acme's employees to Initech.

`test-phase12.ps1` §1 asserts all four outcomes: anonymous 401, wrong-audience 401, wrong **tenant**
403, Acme 200.

## `OriginUserIdentifier`

The connector sets it to the end user's id; this API logs it and echoes it back. That echo exists so
the header is observable from a test, not because a real system would return it.

Its real purpose is the tenant's own audit trail. Without it, everything Acme's logs see is
"`userservice-svc.acme` did something," which cannot answer *who looked at this record* — the question
that actually gets asked after an incident.

## Where this simplifies

- **A `Dictionary` for the directory.** No database, and that is right rather than lazy: this process
  stands in for a system whose storage is none of our business.
- **No `IIdentityContext`, no `ProblemDetails`, no versioned routes.** Every convention this repo
  ports from `Services.Authorization` is a *DIT* convention. Applying them here would quietly imply
  Acme builds services the way DIT does, which is the one thing this project must not imply.
- **It validates DIT-issued tokens.** A real client integration might use an API key, mTLS, or its own
  IdP. Trusting the platform's issuer is the simplest thing that makes the connector's auth mechanism
  (a service-account bearer token) demonstrable, and it *is* one of the real options.

## Verifying it

```powershell
.\run-all.ps1            # this is in the default set - it's on acme's login path
.\test-phase12.ps1
```

It is in `run-all.ps1`'s default set because a login depends on it: `Mini.UserService` resolves acme's
`role` claim through a connector pointed here. With this process down, alice's role silently falls back
to `Member` and `test-phase7.ps1` fails — pointing at `IdentityServerHost`, which is not the broken
process. That is not fragility to design around; it is what taking a dependency on a tenant's own
system costs, and it is worth seeing once.
