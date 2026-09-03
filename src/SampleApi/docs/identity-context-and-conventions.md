# Identity context and API conventions, ported from `Services.Authorization`

`Services.Authorization` is Equisoft's real, production authorization-decision
service — other DIT services ask it "can this caller do X?" over HTTP. This is the
first step of porting its cross-cutting plumbing (not its business logic — see the
scope note below) into `SampleApi`, this sample's own "resource server" role.

**Scope note, read first:** the real service's business logic — `Authorize`/`Evaluate`
endpoints backed by a SQL Server + Redis policy/permission store, a dynamic
`IAuthorizationPolicyProvider` that turns any policy name into a remote permission
check, keyed-DI strategy selection (`AuthorizeServiceFactory`), audit logging, message
queue integration — was **deliberately left out of this port**. What follows is the
identity/claims plumbing and API conventions that sit *underneath* that business logic
in the real service, most of which actually lives in Equisoft's shared
`Libraries.Infrastructure` package (`DIT.Identity`, `DIT.WebApi`), not in
`Services.Authorization` itself. Every section below has a "what's simplified" note;
there's also one combined list at the end for anything cut entirely.

---

## 1. `IIdentityContext` — claims-only multi-tenancy, for a caller with no browser

`Mini.Infrastructure/Identity/IIdentityContext.cs` + `IdentityContext.cs` (moved there in Phase 10):

```csharp
public interface IIdentityContext
{
    bool IsAuthenticated { get; }
    IdentityType IdentityType { get; }
    string? Subject { get; }
    string? ClientId { get; }
    string? TenantKey { get; }
    void Populate(ClaimsPrincipal principal);
}
```

MvcClient's `ITenantContext` (see its own
[`docs/multitenancy-and-external-services.md`](../../MvcClient/docs/multitenancy-and-external-services.md))
resolves tenant from a hostname or a login button, because a browser is involved.
SampleApi never has either — it's a pure API, called server-to-server or from a
browser's `fetch()`, so the **only** thing it ever has to resolve tenant from is the
validated token's own claims. This is exactly how the real `Services.Authorization`
does it too (`Libraries.Infrastructure/DIT.Identity/IdentityContext.cs`) — no hostname,
no UI, just `ClaimsPrincipal`.

### Telling a user apart from a service account

```csharp
Subject = principal.FindFirst("sub")?.Value;
ClientId = principal.FindFirst("client_id")?.Value;
IdentityType = Subject is null ? IdentityType.Service : IdentityType.User;
```

The real service reads an explicit `service_isService` claim
(`Libraries.Infrastructure/DIT.Identity/IdentityPrincipalClaimDefaults.cs`) to tell a
user and a service account apart. IdentityServerHost never stamps an equivalent claim
here — but a `client_credentials` grant has no user behind it at all, so Duende never
puts a `sub` claim on that kind of token either. This sample uses the *absence* of
`sub` as its (accurate, if implicit) stand-in for that explicit flag — you can see this
already documented, informally, in `MvcClient/Controllers/HomeController.cs`'s comment
on `CallApiAsServiceAccount()`: "a client-credentials token has no sub, no name, no
email."

### Resolving tenant — two different claims, for two different reasons

```csharp
TenantKey = IdentityType switch
{
    IdentityType.User => principal.FindFirst("tenant_id")?.Value,
    IdentityType.Service => ClientId?.Split('.', 2) switch
    {
        [_, var tenant] => tenant,
        _ => null
    },
    _ => null
};
```

The real service picks between two claim types depending on caller kind — `tenant` for
a user, `service_tenant` for a service account
(`IdentityPrincipalClaimDefaults.UserTenantClaimType` /
`ServiceTenantClaimType`). This sample's mini IdG only ever stamps one tenant claim
(`tenant_id`, on a user token — see the `tenant` `IdentityResource` in
`IdentityServerHost/Configurations/IdentityServerConfig.json`, Phase 6) and stamps
**nothing** onto a client-credentials token. Instead, tenant is baked into the
client_id's own suffix by convention — `mvcclient-svc.acme`, `mvcclient-svc.globex` — a
pattern this repo already established in that same file's `clients` list for
MvcClient's service-account clients (see the other doc above). `IdentityContext` for a
service caller just parses that suffix instead of reading a claim that doesn't exist
here. Same intent (know which tenant a service account acts for), different mechanism.

**A necessary prerequisite change, not just SampleApi's:** `tenant_id` previously
never reached SampleApi's access token at all — it only reached MvcClient's own ID
token (which MvcClient's `TenantResolutionMiddleware` reads directly off the
signed-in user's cookie, a completely separate claims set).
`IdentityServerConfig.json`'s `api1` `apiResources` entry now lists `"tenant_id"`
alongside `"name"`/`"email"` in `userClaims`, for the same reason those two are there:
an *access token*'s claims come
from the `ApiResource`/`ApiScope` configuration, independent of which `IdentityResource`
scopes were also requested for the ID token.

### `IdentityContextMiddleware` — where this gets populated

```csharp
public class IdentityContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IIdentityContext identityContext)
    {
        identityContext.Populate(context.User);
        await next(context);
    }
}
```

Port of the real `IdentityPrincipalMiddleware`
(`Libraries.Infrastructure/DIT.Identity`), minus its "on-behalf-of" header merge — this
sample has no agent-acting-for-client scenario to model. Positioned in `Program.cs`
exactly where the real one is required to be: **after** `UseAuthentication()` (needs
`context.User` already populated by the JWT Bearer handler) and **before**
`UseAuthorization()` (so anything further down — a policy, a filter — can read
`IIdentityContext` instead of re-deriving claims by hand). `/api/v1/identity`'s handler
and `ServiceAccountOnlyFilter` (§3) both do exactly that.

**Simplification worth naming:** the real service also registers `IIdentityContext` as
a plain scoped DI factory built straight from `IHttpContextAccessor.HttpContext.User`
— no middleware required at all, since the claims are already sitting on
`HttpContext.User` by the time anything asks for it. This sample uses a middleware
instead, matching the idiom `MvcClient/Infrastructure/MultiTenant/TenantResolutionMiddleware.cs`
already established in this repo, so the two client-facing projects read the same way.
Both approaches are correct; this one was chosen for consistency across the repo, not
because it's what the real code does.

---

## 2. API versioning — `Asp.Versioning.Http`

```csharp
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new ApiVersion(1.0);
    options.ReportApiVersions = true;
});

var versionSet = app.NewApiVersionSet().HasApiVersion(new ApiVersion(1.0)).ReportApiVersions().Build();
var api = app.MapGroup("/api/v{version:apiVersion}").WithApiVersionSet(versionSet).HasApiVersion(1.0);

api.MapGet("/identity", ...).RequireAuthorization("ApiScope");
```

`Services.Authorization` versions every controller route the same way
(`[ApiVersion("1.0")]` + `[Route("api/v{version:apiVersion}/[controller]")]`), using
`Asp.Versioning` — the MVC-controller package, since that's a Controllers app.
`SampleApi` is a minimal-API app, so this uses `Asp.Versioning.Http` instead — same
route convention (`api/v{version:apiVersion}/...`), same `ApiVersion` type, same
`ReportApiVersions()` behavior (stamps an `api-supported-versions` response header),
just the minimal-API-shaped registration surface instead of the MVC one.

**What changed as a result:** the endpoint moved from `/api/identity` to
`/api/v1/identity` — a real, breaking route change, not cosmetic. Every caller in this
repo (`MvcClient/Controllers/HomeController.cs`, `ReactSpa/src/App.tsx`, and the
`test-*.ps1` scripts that hit SampleApi directly) was updated to match.

---

## 3. `ServiceAccountOnlyFilter` — gating by identity type, outside the formal policy system

Port of the real `ServiceAccountAuthorizeFilter`
(`Equisoft.AuthorizationService/Infrastructure/Authorization`), which the real service
applies to every management endpoint (`[ServiceFilter(typeof(ServiceAccountAuthorizeFilter))]`
on `BasePoliciesManagementController` and friends) to make sure only a service account
— never a human, however well-permissioned — can bulk-replace policy/role
configuration. The real one is an MVC `IAuthorizationFilter`; minimal APIs have no
controller pipeline, so `IEndpointFilter` is the direct equivalent — it runs in the
endpoint's own filter pipeline and can short-circuit before the handler runs, exactly
like the original:

```csharp
public class ServiceAccountOnlyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var identityContext = context.HttpContext.RequestServices.GetRequiredService<IIdentityContext>();

        if (!identityContext.IsAuthenticated)
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized");
        }

        if (identityContext.IdentityType != IdentityType.Service)
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden");
        }

        return await next(context);
    }
}
```

This mirrors the real filter's exact two-branch shape (`IsAuthenticated` but wrong
type → 403; not authenticated at all → 401) and, like the original, is applied
**instead of** `.RequireAuthorization(...)` — this filter alone decides both
questions, without going through the formal ASP.NET Core authorization-policy system
at all. That's a deliberate, useful contrast with `/api/v1/identity`'s `"ApiScope"`
policy check next to it in `Program.cs`: one endpoint uses the framework's policy
pipeline, the other makes its own decision directly from `IIdentityContext`.

Applied to a new endpoint modeled on the real `CacheController.Delete` — the real
service's other service-account-only example (agents and other services invalidate a
user's cached permissions after a permission change):

```csharp
api.MapDelete("/admin/cache/{tenantKey}", (string tenantKey) => Results.Ok(new
{
    message = $"Cache cleared for tenant '{tenantKey}' (simulated — this sample has no real cache)."
})).AddEndpointFilter<ServiceAccountOnlyFilter>();
```

No real cache exists in this sample, so it just echoes back what it would have
cleared — the point is the gate, not the side effect.

---

## 4. `ProblemDetails` — RFC 7807 error bodies

```csharp
builder.Services.AddProblemDetails();
// ...
app.UseExceptionHandler();
```

The real service has a custom RFC 7807 middleware
(`Libraries.Infrastructure/DIT.WebApi/ProblemDetails/ProblemDetailsMiddleware.cs`) that
catches unhandled exceptions and no-body error responses and writes a
`Microsoft.AspNetCore.Mvc.ProblemDetails` object, preserving CORS headers on the way
out. ASP.NET Core's own built-in `AddProblemDetails()` (available since .NET 8) covers
the same two cases with no custom middleware needed:

- **Unhandled exceptions**, via `app.UseExceptionHandler()` paired with
  `AddProblemDetails()` — turns a crash into a 500 problem+json response instead of a
  blank one or the developer exception page. Nothing in this sample throws on purpose,
  so this isn't exercised by the test script; verified manually instead, by temporarily
  adding a `MapGet("/diagnostics/throw", () => throw new InvalidOperationException(...))`
  endpoint — `GET` on it returned `HTTP 500`, `Content-Type: application/problem+json`,
  a `title`/`status`/`traceId` body. Removed again afterward; it's here because a real
  API needs it, not because this sample demonstrates it permanently.
- **`Results.Problem(...)`**, used explicitly by `ServiceAccountOnlyFilter` (§3) — this
  always produces a problem+json body, regardless of `AddProblemDetails()`.

**A boundary worth naming, found by actually testing this — not assumed from the
docs:** `/api/v1/identity`'s `.RequireAuthorization("ApiScope")` 401 (no token at all)
comes back with an **empty body**, `Content-Length: 0`, despite `AddProblemDetails()`
being registered:

```
$ curl -i https://localhost:5007/api/v1/identity
HTTP/1.1 401 Unauthorized
Content-Length: 0
WWW-Authenticate: Bearer
```

That 401 is a JWT Bearer **authentication challenge**, written directly by
`JwtBearerHandler.HandleChallengeAsync` — a scheme-specific handler that never
consults `IProblemDetailsService` at all. The ASP.NET Core integration
`AddProblemDetails()` actually wires up automatically is narrower than "every 4xx/5xx
in the app": unhandled exceptions via `UseExceptionHandler()` (verified above) and a
small number of specific framework paths — it is **not** a blanket guarantee that
every unauthenticated/forbidden response gets a body. `ServiceAccountOnlyFilter`'s
`DELETE /api/v1/admin/cache/{tenantKey}` 401, by contrast, **does** come back as
problem+json:

```
$ curl -i -X DELETE https://localhost:5007/api/v1/admin/cache/acme
HTTP/1.1 401 Unauthorized
Content-Type: application/problem+json

{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.2","title":"Unauthorized","status":401,"traceId":"..."}
```

— not because of anything automatic, but because the filter calls `Results.Problem(...)`
**explicitly**. The lesson worth carrying forward: don't assume registering
`AddProblemDetails()` alone makes every error response in an app look the same: check
which code path actually produces each one, the way this comparison just did.

---

## Running it

Same terminals as `MvcClient/docs/multitenancy-and-external-services.md#running-it`
(`IdentityServerHost`, `SampleApi`, plus `MvcClient` and/or `ReactSpa` for a real
click-through). Then, without a browser at all:

[`test-sampleapi-identity-context.ps1`](../../../test-sampleapi-identity-context.ps1)
(repo root) drives all four sections above over raw HTTP:

1. Logs in as `alice` via `reactspa` requesting `openid profile api1 tenant` with
   `acr_values=tenant:acme`, calls `GET /api/v1/identity`, and confirms
   `identity.identityType == "User"` and `identity.tenantKey == "acme"` (from the
   `tenant_id` claim).
2. Gets a service-account token for `mvcclient-svc.acme` directly (`client_credentials`,
   no user at all), calls the same endpoint, and confirms `identity.identityType ==
   "Service"`, a **null** `identity.subject`, and `identity.tenantKey == "acme"` —
   this time parsed from the `client_id`, since no `tenant_id` claim exists on this
   token at all.
3. Calls `DELETE /api/v1/admin/cache/acme` three times — no token (401,
   `application/problem+json`), Alice's own user token (403), and the service-account
   token (200) — proving `ServiceAccountOnlyFilter` actually discriminates by identity
   type, not just by "is this request authenticated."

## What's deliberately not ported

- **The `Authorize`/`Evaluate` endpoints and the SQL Server + Redis policy store behind
  them.** This is the real service's entire reason to exist — a caller sends a
  permission-key string, the service resolves it against a `Policy`/`Role`/`Permission`
  graph and returns allow/deny or a flat access-key list. Out of scope for this step;
  see the "Authorize endpoint" option this port's scoping conversation declined.
- **The dynamic `IAuthorizationPolicyProvider` + `PermissionHandler` pair**
  (`Libraries.Infrastructure/DIT.Authorization.Client`) that lets any consuming service
  write `[Authorize(Policy = "shared.eapp.Lock")]` and transparently resolves it via a
  remote call to `Services.Authorization`. SampleApi's own `"ApiScope"` policy is a
  plain static `RequireClaim` check, not a dynamic remote-resolving one.
- **Keyed DI strategy selection** (`AuthorizeServiceFactory` picking between `User`,
  `Service`, `Guest`, `OnBehalfOf` implementations via `GetRequiredKeyedService<T>`) —
  a genuinely clean pattern in the real service, but this sample only ever has two
  identity types and one code path per endpoint, so there's no branching to factor out.
- **`Guest` and `OnBehalfOf` identity types.** This sample only ever sees a logged-in
  user or a client-credentials service account.
- **Redis-backed permission caching, and the distributed-lock cache-stampede
  protection** on the real `EvaluateService` — no caching layer exists in this sample
  at all (there's nothing expensive enough here to need one).
- **Audit logging (`IAuditLogger`) and message-queue integration (RabbitMQ)** on the
  real service's management endpoints — this sample has no management endpoints to
  audit.
- **The `Basic` authentication scheme gating `/health-details`,** and health checks in
  general — no SQL Server/Redis/logging-service dependencies exist in this sample to
  report on.
- **Serilog + the tenant/identity log enricher** (`IdentityLoggerEnricher`) that stamps
  `TenantId`/`Identity_Type` onto every log line in the real service — this sample uses
  the default `Microsoft.Extensions.Logging` console logger, unchanged.

## Try it yourself

1. **See `ProblemDetails` boundary from §4 directly.** Temporarily change
   `ServiceAccountOnlyFilter` back to `Results.Unauthorized()`/`Results.Forbid()`,
   restart SampleApi, and re-run `test-sampleapi-identity-context.ps1` — step 5 now
   fails on the content-type check, with an empty body where the problem+json used to
   be. Put it back afterward.
2. **Add a third identity type.** IdentityServerHost has no concept of a "guest" or
   "on-behalf-of" caller today, but you can simulate one: add a claim like
   `"acting_for": "acme"` to a service-account client's entry in
   `IdentityServerHost/Configurations/IdentityServerConfig.json` (Phase 6 — no longer
   `Config.cs`), re-run `ConfigIngestionTool`, extend `IdentityType` with a value for it,
   and update `IdentityContext.Populate` to check for that claim before falling back to
   the `client_id`-suffix parse. This is the shape (if not the exact mechanism) of what
   the real `OnBehalfOf` identity type does.
3. **Feel the versioning boundary.** Try calling `GET /api/v2/identity` (a version this
   sample never registers) — a plain `404`, since `/api/v{version:apiVersion}` never
   matches a route template for a version nobody declared; the handler never runs.
   Compare that to a successful `GET /api/v1/identity`'s response headers, which carry
   `api-supported-versions: 1.0` — `ReportApiVersions()` at work.
