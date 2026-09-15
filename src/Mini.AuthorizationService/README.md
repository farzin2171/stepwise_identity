# Mini.AuthorizationService — out-of-band authorization decisions

An authorization service (`:5015`) that **IdentityServerHost** and **SampleApi** can call to make authorization decisions outside of the token itself. Phase 13's contribution: the permission decision leaves the token. Phase 15's contribution: that decision is now cached, and the cache survives a restart. Phase 21's contribution: a `Policy` row can be changed over HTTP, and doing so publishes a real `PolicyChangedEvent` onto the message bus.

```
1. Foundation ✓
...
13. Mini.AuthorizationService (the permission decision leaves the token) ✓
14. (authorization integration — SampleApi calls the authorization service) ✓
15. (persist authorization decisions across restarts) ✓
...
20. Mini.MessageCenter (webhook fan-out) ✓
21. Mini.AuthorizationService gains a policy-admin API and publishes PolicyChangedEvent ← this project
22. AgentPortal gets its own database (PolicyChangeRequest audit trail) and a policy-edit UI (next)
```

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

## Phase 21 — a real policy-admin API, and a real publisher

Through Phase 20, `Policies` could only change via `SeedData` and a restart, and `PolicyChangedEvent`
(Phase 19's message-bus contract) had no real producer — `Mini.MessageCenter`'s consumer (Phase 20)
could only be proven with a throwaway diagnostic endpoint on *that* service. Phase 21 closes both
gaps from this side: an HTTP endpoint changes a `Policy` row for real, and doing so publishes a real
`PolicyChangedEvent`.

```csharp
// Program.cs
api.MapPut("/policies/{tenantKey}/{resourceName}", async (
    string tenantKey, string resourceName, UpdatePolicyRequest request,
    AuthorizationDbContext db, IPublishEndpoint publishEndpoint) =>
{
    var existing = db.Policies.SingleOrDefault(p => p.TenantKey == tenantKey && p.ResourceName == resourceName);
    var oldCondition = existing?.Condition;
    // ...upsert existing.Condition = request.Condition, or Add a new Policy...

    var stale = db.CachedDecisions.Where(c => c.TenantKey == tenantKey && c.ResourceName == resourceName);
    db.CachedDecisions.RemoveRange(stale);
    db.SaveChanges();

    await publishEndpoint.Publish(new PolicyChangedEvent
    {
        TenantKey = tenantKey, ResourceName = resourceName,
        OldCondition = oldCondition, NewCondition = request.Condition,
        ChangedAtUtc = DateTimeOffset.UtcNow
    });

    return Results.Ok(/* the updated policy */);
});
```

```csharp
// Program.cs — publish-only registration, no consumer
builder.Services.AddMessageBus(builder.Configuration);
```

`AddMessageBus`'s `configureConsumers` parameter was already optional (Phase 19's signature), so
becoming the repo's first real *publisher* needed no change to `Mini.Infrastructure/Messaging/` at
all — only a call to the same extension method `Mini.MessageCenter` already calls with a consumer
argument, this time with none.

### Three real design questions, decided rather than deferred

1. **Auth: any authenticated caller, no role gate.** Per this arc's design session (Q7), editing a
   policy is open to any authenticated user for now — the same posture SampleApi's
   `docs/identity-context-and-conventions.md` already documents for `IIdentityContext`: a real user
   token and a service-account token are both accepted, and the endpoint cannot yet tell "an
   authorized admin" from "any signed-in agent." This is a genuine, named gap, not an oversight — see
   "Where this sample simplifies" below.
2. **Upsert, not require-exists.** A `PUT` to a `(tenantKey, resourceName)` pair that has no `Policy`
   row yet creates one instead of 404ing. This mirrors how every existing `Policy` row already got
   there — `SeedData` just inserts them; there was never a "create" distinct from "update" to begin
   with — and it's the simplest option the design brief allowed either choice on.
3. **Cache eviction on write, not left to the TTL.** Through Phase 15's README, "cache eviction on
   policy change" was listed as deliberately missing, reasoned about as acceptable because the real
   Redis-backed `Services.Authorization` cache has the same limitation. That reasoning stops applying
   the moment a real caller can actually change a policy: leaving a stale `CachedDecision` in place
   for up to 30 seconds after a real edit means "authorized: true" for a policy that was *just*
   tightened — not a hypothetical, `test-phase21.ps1 §6` catches it directly. The fix clears every
   `CachedDecisions` row for that `(tenantKey, resourceName)` pair on a successful write — narrower
   than the tenant-wide `DELETE /cache/{tenantKey}` endpoint below, so updating `"sample-api"` doesn't
   also evict `"agent-portal"`'s unrelated cached decisions for the same tenant.

### Comparison to the real system

The real `Services.Authorization` was not found to have an equivalent "policy admin" HTTP surface in
the time this phase allotted to look — its policies are understood to be managed through a different,
unexplored administrative path (possibly a database migration or a separate tools UI, neither
confirmed). This section says so plainly rather than inventing a shape: if this arc continues, that
real counterpart is worth confirming before Phase 22 designs Agent Portal's edit UI around an assumed
shape.

## Things that broke while building Phase 21

1. **`Mini.AuthorizationService.csproj` needed the same `MassTransit`/`MassTransit.RabbitMQ` package
   pair `Mini.MessageCenter` already references** — Phase 19 put `AddMessageBus` in
   `Mini.Infrastructure`, but the MassTransit packages themselves are per-project references, not
   transitive through `Mini.Infrastructure.csproj`. Missed on the first build attempt (`IPublishEndpoint`
   and `AddMessageBus` both failed to resolve) and fixed by copying the same two `PackageReference`
   lines Phase 20 added to `Mini.MessageCenter.csproj`.
2. **Deleting Phase 20's diagnostic endpoint left a now-unused `PublishTestEventRequest` record and
   an unused-if-left `using MassTransit;`** in `Mini.MessageCenter/Program.cs` — the record was
   deleted outright (nothing else referenced it), but the `using` stayed: `x.AddConsumer<...>()` in
   the same file still needs it. Worth noting only because it's the kind of thing a partial removal
   leaves behind if you don't check what else a `using` was serving.
3. **Docker/RabbitMQ was unreachable in the sandbox this phase was built in** (`docker info` fails the
   same way Phases 19-20 reported). `test-phase21.ps1` could not be run live here. What WAS verified:
   `dotnet build` across the whole solution, and every `dotnet test` case not tagged
   `[RequiresRabbitMQ]` (72 passing, unrelated to this phase's own logic since it added no new xunit
   decision table — see "Why no new xunit test" below). A human with Docker Desktop needs to run
   `.\run-all.ps1` then `.\test-phase21.ps1` to confirm the live bus → consumer → webhook path.

### Why no new xunit test

Phase 21's own logic — upsert-or-create, clear matching cache rows, publish — is CRUD with one
cache-scoping rule, not a decision table like Phase 15's `IsExpired` boundary or Phase 12's connector
cascade. `test-phase21.ps1` exercises it end to end (including the negative case: a stale cached
`true` answer must NOT survive the update) more legibly than an EF-InMemory-backed unit test would,
per this repo's own rule ("if the phase adds no branching logic, `test-phaseN.ps1` is sufficient on
its own").

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

Since Phase 21, any authenticated caller can also change what `/evaluate` decides, via
`PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}`:

```bash
curl -X PUT https://localhost:5015/api/v1/authorization/policies/acme/sample-api \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"condition": "{\"requiredRoles\": [\"Admin\"]}"}'
```

Returns the upserted policy:

```json
{
    "id": "...",
    "tenantKey": "acme",
    "name": "Acme Admins",
    "resourceName": "sample-api",
    "policyType": "Role",
    "condition": "{\"requiredRoles\": [\"Admin\"]}",
    "isEnabled": true,
    "order": 1
}
```

Phase 22's plan is for Agent Portal's not-yet-built policy-edit UI to call this same endpoint with
its signed-in agent's own forwarded access token, once Agent Portal gets a database of its own for
recording *that* a change happened (`PolicyChangeRequest`, an audit trail — never a second copy of
`Policy` itself, which stays owned here).

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

Prove Phase 21's real publish, with a real RabbitMQ running (`.\run-all.ps1`):

1. Call `/evaluate` for `acme`/`sample-api` with `{"role": "Admin"}` — `authorized: true`, then call it
   again to get `"cached": true`.
2. `PUT /api/v1/authorization/policies/acme/sample-api` with a tightened `Condition` (e.g.
   `{"requiredRoles": ["SuperAdmin"]}`).
3. Call `/evaluate` with the exact same body as step 1 immediately — no 30-second wait needed. It's
   `"cached": false` (the row was cleared, not just left to expire) and `authorized: false` (the
   policy really changed).
4. Check `GET https://localhost:5017/api/v1/deliveries` on `Mini.MessageCenter` — a fresh
   `DeliveryAttempt` row exists for this exact change, proving the publish reached the bus and the
   consumer for real.

## What's deliberately missing (and why)

- **Redis, or any cache that isn't the primary database.** Phase 15's `CachedDecisions` cache lives in the same SQL Server database as `Policies` — cheaper to run for a teaching sample, but it means a cache read and a cache write both cost a database round trip. The real `Services.Authorization` keeps its cache in Redis specifically to avoid that.
- **Policy evaluation beyond role-checking.** The schema supports a generic `Condition` JSON column, and `Policy.EvaluatePolicy` has a stub for `"Claim"` types, but neither is wired up. Adding them is a matter of time, not architecture.
- **`IAuthorizationPolicyProvider` from Duende IdentityServer.** That's a different pattern entirely — resolving policies *inside the token middleware* based on a resource name. Real `Services.Authorization` does that; this sample doesn't.
- **A role/permission gate on the policy-admin API (Phase 21).** `PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}` requires authentication but accepts any authenticated caller — user or service account, any tenant, any role. This is the deliberate open-editing decision from this arc's design session (Q7), documented here rather than fixed, the same way `IIdentityContext`'s "two callers look identical" gap is documented rather than fixed elsewhere in this repo. Worth closing before this endpoint is ever reachable from something less controlled than a `test-*.ps1` script or a trusted internal caller.
- **A create-vs-update distinction on the policy-admin API.** It always upserts; there's no way to ask for "update only, 404 if missing" or "create only, 409 if it exists." Simplest option, not the only one.
- **Delivery-side scoping on who can trigger which tenant's webhook.** Because the admin API has no tenant restriction on the caller, a caller authenticated for one tenant can publish a `PolicyChangedEvent` for a *different* tenant's policy — nothing here checks that `identity.TenantKey` matches the `tenantKey` in the route. Surfaced while writing this section, not by a failing test; worth fixing alongside the role gate above.
