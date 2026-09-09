# Mini.AuthorizationService — out-of-band authorization decisions

An authorization service (`:5015`) that **IdentityServerHost** and **SampleApi** can call to make authorization decisions outside of the token itself. Phase 13's contribution: the permission decision leaves the token.

Until now, authorization has been embedded in tokens at issuance time — the `role` claim added by `SampleProfileService` lives *inside* the JWT for its lifetime. That works well for static, immutable decisions made at login. For decisions that need to change without reissuing tokens, or policies that are too expensive to recompute for every API call, or policies that need context beyond what fits in a claim, a service-of-services pattern emerges: the API calls back to a policy-evaluation service, sends the current caller's identity, and asks "am I authorized for this?"

This service provides exactly that shape:

```
SampleApi → POST /api/v1/authorization/evaluate
            { "resourceName": "sample-api", "context": { "role": "Admin" } }
            ← { "authorized": true, "reason": "Policy 'Acme Admins' granted access" }
```

The caller identity is extracted from the bearer token (it's a real JWT validation, not a claim trust), and the evaluation is tenant-scoped: "check policies for *my* tenant."

## Database

One table:
- **`Policies`** — per-tenant authorization policies. Each row names a resource, describes who can access it, and in what order to check multiple policies.

The schema is intentionally minimal — just enough to show that policies are *rows*, not code. The real `Services.Authorization` has a far more complex SQL Server + Redis policy store with dynamic `IAuthorizationPolicyProvider`, policy caching, versioning, and more. This sample evaluates every request fresh (no cache) and supports exactly two policy types: Role-based (does the caller have this role?) and — as a placeholder for extensibility — Claim-based.

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

Currently, this service is NOT called by the platform automatically — SampleApi still uses claims from the token. Phase 13 adds the endpoint and proves it works; Phase 14 will integrate it into the login flow so that `SampleApi` calls it instead of reading the `role` claim directly. That integration is deliberately deferred: the service exists and is wired up, but the actual call site doesn't use it yet — the same separation Phase 11 made with `Mini.UserService`, where the service was built and verified before the IdentityServerHost dependency changed.

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

## What's deliberately missing (and why)

- **Dynamic policy changes without re-reading the database.** Every call hits the database; no cache. The real `Services.Authorization` keeps policies in Redis and refreshes from SQL Server on a schedule.
- **Policy evaluation beyond role-checking.** The schema supports a generic `Condition` JSON column, and `Policy.EvaluatePolicy` has a stub for `"Claim"` types, but neither is wired up. Adding them is a matter of time, not architecture.
- **`IAuthorizationPolicyProvider` from Duende IdentityServer.** That's a different pattern entirely — resolving policies *inside the token middleware* based on a resource name. Real `Services.Authorization` does that; this sample doesn't.
- **Admin endpoints to create/update policies.** Mini.UserService has a management API (`POST /api/v1/management/tenants`); this service has only a read-only query (`GET /api/v1/authorization/policies`). Adding an admin `POST` is straightforward; this sample leaves it out on purpose.
