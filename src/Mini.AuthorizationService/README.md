# Mini.AuthorizationService — out-of-band authorization decisions

An authorization service (`:5015`) that **IdentityServerHost** and **SampleApi** can call to make authorization decisions outside of the token itself. Phase 13's contribution: the permission decision leaves the token. Phase 15's contribution: that decision is now cached, and the cache survives a restart.

Until now, authorization has been embedded in tokens at issuance time — the `role` claim added by `SampleProfileService` lives *inside* the JWT for its lifetime. That works well for static, immutable decisions made at login. For decisions that need to change without reissuing tokens, or policies that are too expensive to recompute for every API call, or policies that need context beyond what fits in a claim, a service-of-services pattern emerges: the API calls back to a policy-evaluation service, sends the current caller's identity, and asks "am I authorized for this?"

This service provides exactly that shape:

```
SampleApi → POST /api/v1/authorization/evaluate
            { "resourceName": "sample-api", "context": { "role": "Admin" } }
            ← { "authorized": true, "reason": "Policy 'Acme Admins' granted access" }
```

The caller identity is extracted from the bearer token (it's a real JWT validation, not a claim trust), and the evaluation is tenant-scoped: "check policies for *my* tenant."

## Database

Two tables:
- **`Policies`** — per-tenant authorization policies. Each row names a resource, describes who can access it, and in what order to check multiple policies.
- **`CachedDecisions`** (Phase 15) — the last decision made for a (tenant, caller, resource, context) combination, with an expiry. `POST /evaluate` checks here first.

The schema is intentionally minimal — just enough to show that policies are *rows*, not code. The real `Services.Authorization` has a far more complex SQL Server + Redis policy store with dynamic `IAuthorizationPolicyProvider`, policy caching, versioning, and more. This sample supports exactly two policy types: Role-based (does the caller have this role?) and — as a placeholder for extensibility — Claim-based.

## Phase 15 — a persisted decision cache

Through Phase 14, `POST /evaluate` re-ran every `Policy` for every call — cheap here, but the README
for Phase 13 named it explicitly as what's missing: "no cache." Phase 15 adds one, `CachedDecisions`,
with two properties worth being explicit about:

1. **It's cache-aside, not write-through.** `/evaluate` looks for a matching, unexpired row first; on
   a miss (or an expired row) it evaluates policies as before, then writes the result back with a
   30-second TTL (`DecisionCache.Ttl`). A policy change takes up to 30 seconds to take effect for a
   caller/resource/context combination already cached — the same trade real Redis-backed policy
   caches make.
2. **It's a database table, not a `Dictionary`.** The whole point of this phase is "persist across
   restarts": stop `dotnet run` and start it again, and a still-unexpired cached decision is still
   there, still returned with `"cached": true`, without re-evaluating a single `Policy` row. An
   in-process dictionary would have gone back to zero.

The cache key is `(TenantKey, SubjectKey, ResourceName, ContextHash)` — `SubjectKey` is the caller's
`ClientId` (service accounts) or `Subject` (user tokens), and `ContextHash` is a SHA-256 of the
evaluation context's key/value pairs, order-independent (see `DecisionCache.ComputeContextHash`).
Two different `context` dictionaries for the same caller/resource are two different cache entries —
answering "is Alice authorized as Admin" must never be reused to answer "is Alice authorized as
Member."

`DELETE /api/v1/authorization/cache/{tenantKey}` clears every cached row for a tenant early, gated to
service accounts the same way `SampleApi`'s `admin/cache` endpoint is (`ServiceAccountOnlyFilter`, no
`.RequireAuthorization()`). `SampleApi`'s own `DELETE /admin/cache/{tenantKey}` — a no-op simulation
since Phase 14, because there was no cache to clear — now forwards to this endpoint instead.

The one branching rule this phase adds — is a cached row still valid, given its expiry and the
current time — is table-tested directly in `DecisionCacheTests.cs`, rather than only exercised
end-to-end (a 30-second wait in a `.ps1` script would make `test-phase15.ps1` slow for no benefit).

## Things that broke, and why they're worth knowing

Phase 14's "integration" had never actually been exercised end-to-end against the real, running
services before Phase 15 tried to (test scripts had been written and committed, but the calls
they describe couldn't succeed). Three separate bugs, all caught only by actually running
`run-all.ps1` and hitting the endpoints for real:

1. **`IAuthorizationClient`'s DI factory called `services.GetRequiredService<IHttpClientFactory>()`,
   but `builder.Services.AddHttpClient()` was never called in `SampleApi/Program.cs`.** The factory
   method also never used the client it built from that call — it constructed a second `HttpClient`
   from a raw `HttpClientHandler` and returned that instead. The first `HttpClient` was dead code;
   fixing this was deleting it, not registering `AddHttpClient()`.
2. **`SampleApi` forwarded no bearer token when calling `/evaluate`**, and the endpoint requires one
   (`.RequireAuthorization()` on the whole `/api/v1/authorization` group). Every real call failed
   with 401, which `AuthorizationClient.EvaluateAsync`'s catch-all turned into a quiet
   `authorized: false, reason: "Authorization service error"` — never surfaced as a crash, just as a
   wrong answer. The fix forwards the caller's own inbound token as-is (see `Program.cs`): it's the
   only token that actually carries the identity `/evaluate` needs to answer per-tenant. That in turn
   meant this service's accepted audience needed to widen from just `"authapi"` (service accounts
   calling directly, `test-phase13.ps1`'s pattern) to `["authapi", "api1"]` (a forwarded user token).
3. **`Policy.EvaluatePolicy` checked `identity.Subject != null` to decide whether to look up a role**,
   which is only ever true for a user token — a service-account token has `ClientId`, not `Subject`.
   Every service-account call was silently denied regardless of policy, caught by
   `test-phase13.ps1 §5` (a service account, not a user, evaluating an Admin-role policy) failing
   once the two bugs above stopped masking it. Fixed by checking `identity.IsAuthenticated` instead —
   any authenticated caller, not just ones with a `sub` claim.

None of these are Phase 15's own logic (the cache-aside code and its TTL rule); they're pre-existing
gaps in the call path Phase 15 needed to actually work in order to prove the cache does what it
claims. `IdentityServerConfig.json`'s `userservice-svc.{tenant}` clients also gained `authapi` in
their `allowedScopes` — `test-phase13.ps1 §3` requests exactly that scope and had never been able to
get it.

## The evaluate endpoint

```csharp
POST /api/v1/authorization/evaluate
Bearer <token>

{
    "resourceName": "sample-api",
    "subject": "alice",
    "action": "Read",
    "context": {
        "role": "Admin",
        "tenant": "acme"
    }
}
```

Returns 200 (always — authorization failures are not 403, they're a successful evaluation that says `"authorized": false`):

```json
{
    "authorized": true,
    "reason": "Policy 'Acme Admins' granted access"
}
```

or:

```json
{
    "authorized": false,
    "reason": "No applicable policies granted access"
}
```

## How it's called

Since Phase 14, `SampleApi`'s `POST /api/v1/authorize/{resourceName}` calls this service's
`/evaluate` endpoint per request — see `src/SampleApi/README.md`'s Phase 14 section. `SampleApi`
never reads the `role` claim to make the decision itself; it only forwards it as context.

## Running it

The service requires no external setup beyond the database (created on first run):

```bash
cd src/Mini.AuthorizationService
dotnet run --urls https://localhost:5015
```

Verify it's listening:

```bash
curl -i https://localhost:5015/health
# HTTP/1.1 200 OK
```

Or call the evaluation endpoint with a real token (obtained from a login against `:5001`):

```bash
curl -X POST https://localhost:5015/api/v1/authorization/evaluate \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"resourceName": "sample-api", "context": {"role": "Admin"}}'
```

## Try it yourself

Prove the cache survives a restart — the whole point of Phase 15:

1. Call `/evaluate` twice with the same token and body (as above). The first response is
   `"cached": false`; the second is `"cached": true`, with the same `authorized`/`reason`.
2. Stop the service (`Ctrl+C`) and start it again (`dotnet run --urls https://localhost:5015`).
3. Call `/evaluate` a third time with the exact same token and body, within 30 seconds of step 1.
   It's still `"cached": true` — the row survived the restart because it lives in `MiniAuthorization`,
   not in the process that just restarted.
4. Wait past the 30-second TTL and call it again: `"cached": false` — the row is still there, but
   `IsExpired` says it's stale, so `/evaluate` re-runs the policy and overwrites it.

## What's deliberately missing (and why)

- **Redis, or any cache that isn't the primary database.** Phase 15's `CachedDecisions` cache lives in the same SQL Server database as `Policies` — cheaper to run for a teaching sample, but it means a cache read and a cache write both cost a database round trip. The real `Services.Authorization` keeps its cache in Redis specifically to avoid that.
- **Cache eviction on policy change.** Updating a `Policy` row does not clear the `CachedDecisions` rows that depended on it — callers wait out the 30-second TTL. The real system's Redis cache has the same problem in practice; this sample doesn't pretend to solve it either.
- **Policy evaluation beyond role-checking.** The schema supports a generic `Condition` JSON column, and `Policy.EvaluatePolicy` has a stub for `"Claim"` types, but neither is wired up. Adding them is a matter of time, not architecture.
- **`IAuthorizationPolicyProvider` from Duende IdentityServer.** That's a different pattern entirely — resolving policies *inside the token middleware* based on a resource name. Real `Services.Authorization` does that; this sample doesn't.
- **Admin endpoints to create/update policies.** Mini.UserService has a management API (`POST /api/v1/management/tenants`); this service has only a read-only query (`GET /api/v1/authorization/policies`). Adding an admin `POST` is straightforward; this sample leaves it out on purpose.
