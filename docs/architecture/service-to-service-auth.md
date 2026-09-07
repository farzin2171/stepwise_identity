# Service-to-service authentication

**Current state as of Phase 11.** How one process in this repo proves who it is to another when there
is no user and no browser involved.

There are **three** such mechanisms, and they are not interchangeable. Picking the wrong one is not a
style question — two of them differ in whether the credential can be revoked.

## The three mechanisms

| | Self-issued JWT | Service-account token | Duende local API |
| --- | --- | --- | --- |
| Who uses it | IdentityServerHost → Mini.UserService (reads, during token issuance) | MvcClient → SampleApi; Mini.UserService → IdentityServerHost; anything → Mini.UserService's management API | IdentityServerHost protecting its own `/api/user/convert` |
| How the token is obtained | `IIdentityServerTools.IssueClientJwtAsync` — signed in-process, no round trip | `POST /connect/token`, `grant_type=client_credentials`, with a registered client id and secret | *(not a way to get a token — a way to validate one)* |
| Registered client needed | **No.** Nothing in `IdentityServerConfig.json` mentions `identityserverhost` | **Yes**, one per caller, with a secret | n/a |
| Claims it carries | `iss`, `nbf`, `iat`, `exp`, `client_id`, `aud` — and nothing else | the above plus `scope`, and `aud` from the requested scopes' API resources | n/a |
| Can it be revoked without a deploy | No — revoking it means changing the signing key | Yes — delete the client or rotate its secret | n/a |
| Validated by | JWT Bearer against the published JWKS | JWT Bearer against the published JWKS | IdentityServer's own `ITokenValidator`, in-process |

### Self-issued JWT — for reads, and it is what the real IdG does

```csharp
var jwt = await tools.IssueClientJwtAsync(
    tenantOptions.JwtAuthentication.ClientId,   // "identityserverhost"
    lifetime: 300,
    ct,
    audiences: [tenantOptions.JwtAuthentication.Audience]);   // "tenantmgntapi" / "userapi"
```

IdentityServerHost signs a token for itself with the same key it signs every other token with, and
Mini.UserService accepts it for the same reason SampleApi accepts anything: it validates against the
published JWKS. There is no OAuth round trip and no client registration, which is precisely why it is
fast enough to sit in the token-issuance path — every login makes two of these calls.

**Phase 10 predicted Phase 11 would replace this with a service-account token. It didn't, and the
prediction was wrong on the facts.** Phase 7 verified that `IssueClientJwtAsync` is the *exact* pattern
the real `TenantClient`/`UserClient` use. Replacing it would have made the sample less faithful, not
more. What Phase 11 actually did was add the service-account path *alongside* it, where the real system
genuinely uses one.

### Service-account token — for writes, and for anything crossing a trust boundary

```
POST /connect/token
grant_type=client_credentials&client_id=userservice-svc.acme&client_secret=…&scope=IdentityServerApi
```

The client id is `{ClientId}.{tenantKey}`, built by
[`Mini.Infrastructure/ExternalServices/TokenClient.cs`](../../src/Mini.Infrastructure/ExternalServices/TokenClient.cs),
with a **per-tenant secret**. That shape is the point: revoking Initech's service-account access never
touches Acme's.

The registered service accounts in this repo:

| Client | Scopes | Used by | For |
| --- | --- | --- | --- |
| `mvcclient-svc.acme` / `.globex` | `api1` | MvcClient | calling SampleApi with no user |
| `userservice-svc.acme` / `.globex` / `.initech` | `IdentityServerApi` | Mini.UserService | calling IdentityServerHost's `/api/user/convert` |
| `userservice-mgmt-svc` | `tenantmgntapi`, `userapi` | operators, `test-phase11.ps1` | writing to Mini.UserService's management API |

`userservice-mgmt-svc` has **no tenant suffix**, and that absence is worth noticing: creating a tenant
is inherently cross-tenant, so there is no tenant to name. `IIdentityContext` therefore resolves its
`TenantKey` to `null` — this repo's suffix convention has no way to say "all tenants" as distinct from
"none." The real `DIT.Identity` reads an explicit `service_tenant` claim, which can simply be absent.

### Duende local API — for an API hosted inside the IdentityServer process

`AddLocalApiAuthentication()` validates a token through IdentityServer's own `ITokenValidator`, in
process. The alternative — pointing a JWT Bearer handler at `Authority = "https://localhost:5001"` from
inside `:5001` — works, but makes the host fetch its own discovery document over HTTP from itself to
answer a question it can already answer locally.

One behavioural difference matters and is asserted in `test-phase11.ps1` §7: the local-API handler
checks the expected scope during **authentication**, so a valid token *without* `IdentityServerApi`
gets **401**, not 403. It is indistinguishable from no token at all. SampleApi's model (JWT Bearer
authenticates, then a policy forbids) gives 403 for the same situation. If you are debugging a 401 from
`/api/user/convert` while holding a token you know is valid, the scope is what to check.

## The bidirectional loop

```
                    role + tenant lookup, self-issued JWT (aud: userapi / tenantmgntapi)
        ┌──────────────────────────────────────────────────────────────────┐
        │                                                                  ▼
┌───────────────────────┐                                    ┌──────────────────────┐
│  IdentityServerHost   │                                    │   Mini.UserService   │
│        :5001          │                                    │        :5013         │
│  db: MiniIdG          │◀───────────────────────────────────│  db: MiniUsers       │
└───────────────────────┘   id conversion, service-account   └──────────────────────┘
                            token (userservice-svc.{tenant},
                            scope: IdentityServerApi)
```

Both arrows are real in production. `Services.User`'s own analysis puts it plainly: *"So User ⇄ IDG is
bidirectional: IDG calls User for the role claim; User calls IDG for id conversion."*

The two directions carry different credentials **because they cross the boundary for different
reasons**. The IdG's call is a hot-path read on every login, from a process that owns the signing key
anyway. The User service's call is a cold-path lookup from a process that is genuinely a separate
security principal — so it uses a credential that can be revoked without redeploying either side.

### The cycle is real, and does not deadlock — here

Mini.UserService asks IdentityServerHost's `/connect/token` for a token before calling it. That token
request is a `client_credentials` grant with no subject, so Duende does not invoke `IProfileService`
for it — and `SampleProfileService` is what would call back into Mini.UserService. The loop closes
because *client-credentials token issuance does not enrich claims*.

That is a property of the current design, not a guarantee. Anything that makes token issuance call
Mini.UserService unconditionally — including for client-credentials grants — reintroduces the cycle.

## Where this sample simplifies

- **No `service_isService` claim.** The real `DIT.Identity` stamps an explicit caller-kind claim.
  `Mini.Infrastructure`'s `IdentityContext` infers "service" from the *absence* of `sub` instead, which
  turns out to be the weaker rule — see the next section.
- **Secrets are in `appsettings.Development.json`.** A real deployment pulls them from a vault. They
  are in the file here for the same reason `IdentityServerConfig.json` carries plaintext secrets: this
  sample must run for anyone with nothing but nuget.org and LocalDB.
- **No mutual TLS, no DPoP, no token binding.** Bearer tokens throughout.
- **No audit trail.** The real management APIs publish `CREATE_TENANT` / `DELETE_TENANT` audit events
  via `AddAudit()`. Nothing here records who created what.

## The gap this phase found: two identity types, three kinds of caller

`Mini.Infrastructure`'s `IdentityType` has two values, `User` and `Service`, and `ServiceAccountOnlyFilter`
decides purely on that enum. But this repo now has **three** kinds of caller:

1. a user — has `sub`
2. a registered service account — no `sub`, has `scope`
3. IdentityServerHost issuing itself a token — no `sub`, **no `scope`**

Kinds 2 and 3 are indistinguishable to `IdentityContext`. Verified, not assumed: pointing `UserClient`
at Mini.UserService's `/api/v2/identity` diagnostic returns

```json
{"identityType":"Service","subject":null,"clientId":"identityserverhost","tenantKey":null}
```

for the self-issued read token — same verdict a registered service account gets, and the same `userapi`
audience the management surface uses. So with `ServiceAccountOnlyFilter` alone, **the token the IdG
sends to read a role would have been accepted to write one.**

Mini.UserService closes it by requiring the `scope` claim on its management policies, which the
self-issued JWT does not have and cannot get without going through `/connect/token` as a registered
client. That fix was verified in both directions: with the read policy the probe returned 200, with the
management policy it returns 403.

It is the narrow fix, not the general one. The general fix is the real system's: model caller *kinds*
explicitly instead of inferring them, so a host issuing itself a token is a third thing rather than an
under-specified second thing. That would mean a third `IdentityType`, and it is left undone on purpose
— the gap is more instructive documented than quietly closed.
