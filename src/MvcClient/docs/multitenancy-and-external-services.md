# Multi-tenancy, IdentityGatewayApi, and ExternalServicesApi

This is the first step of porting `Applications.Apply`'s (Equisoft's real, production
MVC BFF) multi-tenancy infrastructure and its two backend-integration patterns —
`IdentityGatewayApi` and `ExternalServicesApi` — into this teaching sample. Both of
those are *real* configuration-section names in Apply's own `appsettings.json`, not
names invented for this port.

**Scope note, read first:** Apply's real implementation is backed by SQL Server, EF
Core, Autofac, MassTransit, and a distributed cache — a genuinely production-grade
system. This sample has none of those (consistent with everything else in this repo),
so what follows is the *shape* of Apply's patterns without their storage layer. Every
section below has a "what's simplified" note; there's also one combined list at the end
for anything cut entirely.

---

## 1. Multi-tenancy infrastructure

### The `Tenant` model — `Infrastructure/MultiTenant/Tenant.cs`

```csharp
public class Tenant
{
    public required string Key { get; init; }
    public required string Name { get; init; }
}
```

Apply's real `Tenant` is an EF entity (SQL-backed, with a `Metadata` key-value
collection for arbitrary per-tenant settings). This is the same shape without the
database — the same simplification this whole repo makes everywhere else.

### The registry — `Infrastructure/MultiTenant/Tenants.cs`

A hard-coded `Dictionary<string, Tenant>` (`acme`/`globex`), deliberately **separate**
from `IdentityServerHost/Tenants.cs`'s own registry, not a shared reference to it. This
mirrors something real about Apply's architecture worth sitting with: Apply owns its
own SQL `Tenants` table, entirely independent from the IdG's own tenant registry. The
two are kept in sync by an ops process, not by sharing code. A tenant key that exists in
one system but not the other is a real, meaningful failure mode — and now a reproducible
one in this sample too, by editing just one of the two `Tenants.cs` files.

### `ITenantContext` / `TenantContext` — the ambient holder

```csharp
public interface ITenantContext
{
    Tenant? Tenant { get; }
    void SetTenant(Tenant tenant);
}
```

Registered **scoped** (`Program.cs`) — one instance per request. Written once by
`TenantResolutionMiddleware`, read by everything downstream (`HomeController`,
`CallApiAsServiceAccount`, the `Secure` view) without threading a tenant parameter
through every method signature. This is a verbatim port of Apply's
`Equisoft.Apply.Domain/Identity/{ITenantContext,TenantContext}.cs` — same shape, same
"one writer, get-only from the outside" design.

### `TenantResolutionMiddleware` — where this sample's shape genuinely differs, not just simplifies

```csharp
public class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tenantKey = context.User.FindFirst("tenant_id")?.Value;
            var tenant = Tenants.Find(tenantKey);
            if (tenant is not null)
            {
                tenantContext.SetTenant(tenant);
            }
        }

        await next(context);
    }
}
```

Apply's real `MultiTenantMiddleware` resolves tenant from the **request itself**,
before anyone has necessarily logged in — from the **hostname** for a browser request,
or from a JWT claim for an API request. Apply's tenant is a property of *which domain
you're visiting*.

This sample kept Phase 3's explicit **"Log in as Acme Corp" / "Log in as Globex
Corporation"** button flow instead of switching to hostname-based routing (see the
"Try it yourself" section for what that would take). That was a deliberate choice, not
an oversight — see this project's README's earlier phases for why those buttons exist.
The consequence: there is no tenant to resolve *before* login in this sample, because
the tenant only becomes known once IdentityServerHost hands back a `tenant_id` claim on
the authenticated user. So this middleware resolves tenant the way Apply's **own** code
resolves it for *API* requests (from a JWT claim) — applied here to every request,
because in this sample that's the only resolution source that exists at all.

### `RequireTenantAttribute` — the enforcement filter, and its honest gap

```csharp
public class RequireTenantAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.User.Identity?.IsAuthenticated != true) return;

        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenantContext.Tenant is null)
        {
            context.Result = new UnauthorizedResult();
        }
    }
}
```

Apply's real `TenantIdentificationFilter` compares **two independently resolved**
tenants — the host-resolved one and the `tenant`/`service_tenant` claim on the
authenticated user — and rejects on mismatch. *That* comparison is the actual security
boundary in Apply: it's what stops a user authenticated for Tenant A from sliding under
Tenant B's hostname.

This sample only ever has **one** resolution source (the claim itself), so there is no
independent second source to cross-check against — a "mismatch" in Apply's sense simply
can't happen here. What this filter still meaningfully checks: that an authenticated
user's tenant actually resolved to *something* in `Tenants.All` at all, failing closed
instead of silently proceeding with `Tenant = null`. **The real dual-source
cross-check is not implemented — see "Try it yourself" for what adding it (hostname
resolution) would require.**

Applied to `Secure()`, `CallApi()`, and `CallApiAsServiceAccount()` in
`HomeController.cs`.

---

## 2. IdentityGatewayApi

Like Apply, there is **no single "IdentityGatewayApi" class** — it's the real
`appsettings.json` section name, consumed two different ways.

### a) Tenant-aware OIDC redirect

`Infrastructure/Configuration/IdentityGatewayConfiguration.cs`:

```csharp
public class IdentityGatewayConfiguration
{
    public string Url { get; set; } = string.Empty;
    public Dictionary<string, string> TenantUrls { get; set; } = new();
    public string ClientId { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;

    public string GetRequestUri(string tenantKey) =>
        TenantUrls.TryGetValue(tenantKey, out var tenantUrl) && !string.IsNullOrWhiteSpace(tenantUrl)
            ? tenantUrl
            : Url;
}
```

A verbatim port of `Equisoft.Apply.Domain/Configuration/IdentityGatewayConfiguration.cs`
— same field names, same fallback logic.

In `Program.cs`'s `OnRedirectToIdentityProvider` handler:

```csharp
if (context.Properties.Items.TryGetValue("tenant", out var tenantKey) && tenantKey is not null)
{
    var identityGatewayConfiguration = context.HttpContext.RequestServices
        .GetRequiredService<IOptions<IdentityGatewayConfiguration>>().Value;
    var requestUri = identityGatewayConfiguration.GetRequestUri(tenantKey);

    context.ProtocolMessage.IssuerAddress = $"{requestUri}/connect/authorize";
    context.ProtocolMessage.AcrValues = $"tenant:{tenantKey}";
}
```

This is the same event hook (and the same two responsibilities — pick the
tenant-correct Authority URL, stamp `acr_values=tenant:{key}`) as Apply's real
`Infrastructure/Authentication/Functions/OpenIdConnectFunctions.cs`'s
`RedirectToIdentityProviderFunction`. The one real difference: Apply's version fires
automatically on **every** challenge (because tenant is already known from the
hostname, before the challenge even starts); this sample's version only has a tenant to
work with when `HomeController.LoginAsTenant()` explicitly puts one in
`AuthenticationProperties.Items` first.

**In this sample, `TenantUrls` is empty for both tenants** — there's only one
IdentityServerHost, so `GetRequestUri` always falls back to `Url`. The mechanism is
real and testable anyway — see "Try it yourself."

**Simplification worth naming:** overriding the *authorize* URL per tenant is
implemented; the *token validation* side (checking a received token's issuer/audience
against the tenant-correct authority) is not — this sample's single `options.Authority`
stays fixed regardless of which tenant's authorize endpoint was actually used. Not an
issue here since every tenant shares the same IdentityServerHost anyway; a real
multi-authority deployment would need this addressed too.

### b) Service-account (client-credentials) token client

`Infrastructure/Configuration/ServiceAccount.cs`:

```csharp
public class ServiceAccount
{
    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public Dictionary<string, string> TenantSecrets { get; set; } = new();
}
```

Notice this lives *nested inside* `ExternalServicesConfiguration` (§3), not
`IdentityGatewayConfiguration` — even though the token endpoint is the IdG's own. That's
not a mistake in this port; it's a verbatim match of Apply's real, slightly confusing
`appsettings.json` layout.

`Infrastructure/Externals/TokenClient.cs`:

```csharp
public async Task<string> GetAccessTokenAsync(ServiceAccount serviceAccount, string tenantKey, CancellationToken ct = default)
{
    var clientId = $"{serviceAccount.ClientId}.{tenantKey}";   // e.g. "mvcclient-svc.acme"
    // ... cache check ...
    var clientSecret = serviceAccount.TenantSecrets[tenantKey];

    var response = await httpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
    {
        Address = serviceAccount.TokenEndpoint,
        ClientId = clientId,
        ClientSecret = clientSecret
    }, ct);
    // ... cache the token, return it ...
}
```

The real `client_id` sent to `/connect/token` is `"{ServiceAccount.ClientId}.{tenantKey}"`
— e.g. `mvcclient-svc.acme` — with a **per-tenant secret**. This is why
`IdentityServerHost/Config.cs` now registers **two** client-credentials clients instead
of one:

```csharp
new Client
{
    ClientId = "mvcclient-svc.acme",
    ClientSecrets = { new Secret("acme-svc-secret".Sha256()) },
    AllowedGrantTypes = GrantTypes.ClientCredentials,
    AllowedScopes = { "api1" }
},
new Client
{
    ClientId = "mvcclient-svc.globex",
    ClientSecrets = { new Secret("globex-svc-secret".Sha256()) },
    AllowedGrantTypes = GrantTypes.ClientCredentials,
    AllowedScopes = { "api1" }
}
```

A real deployment gives every tenant its own client-credentials client and secret, so
revoking or rotating one tenant's service-account access never touches another
tenant's. No user is involved in this grant at all — there's no browser redirect, no
login page, just a direct server-to-server POST.

**Simplification worth naming:** the real `ServiceAccountTokenRepository` caches in
`IDistributedCache` and re-validates freshness by *decoding the cached JWT's own `exp`
claim* on every read. This sample's `TokenClient` uses `IMemoryCache` with an absolute
expiration set from the token response's own `expires_in` instead — same effect (a
request past expiry always fetches fresh), fewer moving parts, no JWT parsing needed.

---

## 3. ExternalServicesApi

Also not one class — a **config-driven registry**. Apply's real one holds six DIT
service definitions (`Configuration`, `Authorization`, `Localization`,
`UserExperience`, `User`, `AssistantManagement`); this sample's holds exactly **one**
(`SampleApi`) — same pattern, smaller registry.

`Infrastructure/Configuration/ServiceDefinition.cs` + `ExternalServicesConfiguration.cs`:

```csharp
public class ServiceDefinition
{
    public string Path { get; set; } = string.Empty;
    public string HealthPath { get; set; } = string.Empty;
    public string? BaseUri { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }
    public string GetFullPath() => $"{BaseUri}{Path}";
}

public class ExternalServicesConfiguration
{
    public string? BaseUri { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }
    public Dictionary<string, ServiceDefinition> ServiceDefinitions { get; set; } = new();

    public ServiceDefinition GetServiceDefinition(string serviceName)
    {
        var serviceDefinition = ServiceDefinitions[serviceName];
        serviceDefinition.ServiceAccount ??= ServiceAccount;   // fallback to the global service account
        if (string.IsNullOrWhiteSpace(serviceDefinition.BaseUri))
            serviceDefinition.BaseUri = BaseUri;               // fallback to the global base URI
        return serviceDefinition;
    }
}
```

Verbatim port of `Equisoft.Apply.Domain/Configuration/{ServiceDefinition,ExternalServicesConfiguration}.cs`
— same fallback logic (a service definition inherits the registry's global
`BaseUri`/`ServiceAccount` when it doesn't set its own).

### Config shape (`appsettings.Development.json`)

```json
"ExternalServicesApi": {
  "BaseUri": "",
  "ServiceAccount": {
    "TokenEndpoint": "https://localhost:5001/connect/token",
    "ClientId": "mvcclient-svc",
    "TenantSecrets": { "acme": "acme-svc-secret", "globex": "globex-svc-secret" }
  },
  "ServiceDefinitions": {
    "SampleApi": { "BaseUri": "https://localhost:5007", "Path": "", "HealthPath": "" }
  }
}
```

### The named `HttpClient`, now config-driven, with Apply's own resilience pattern

`Program.cs`:

```csharp
builder.Services.AddHttpClient("SampleApi", (services, client) =>
       {
           var externalServices = services.GetRequiredService<IOptions<ExternalServicesConfiguration>>().Value;
           var serviceDefinition = externalServices.GetServiceDefinition("SampleApi");
           client.BaseAddress = new Uri(serviceDefinition.GetFullPath());
       })
       .AddPolicyHandler(RetryPolicy())
       .AddPolicyHandler(CircuitBreakerPolicy());
```

Before this port, `client.BaseAddress` was a hardcoded string literal. Now it comes
from the same config-driven registry Apply uses for all six of its real service
clients. The retry (exponential backoff, 2 attempts) and circuit-breaker (trip after 3
consecutive failures, 30-second break) policies are the same shape as
`Infrastructure/Http/ServiceCollectionExtensions.cs`'s `ConfigureHttpClients` in the
real Apply — applied here to **both** named clients this app has (`"SampleApi"` and
`"token"`, the latter used by `TokenClient` to reach IdentityServerHost's token
endpoint).

### Two calling patterns, side by side — `HomeController.cs`

Apply's six real clients split into two authentication patterns. This sample now
demonstrates both against the same endpoint, so the difference is directly visible
rather than something you have to take on faith:

| | `CallApi()` | `CallApiAsServiceAccount()` |
|---|---|---|
| Apply counterpart | `AuthorizationServiceClientV1`, `AssistantManagementServiceClient` | `ConfigurationServiceClientV1`, `UserServiceClient` |
| Token used | The signed-in user's own access token (`HttpContext.GetTokenAsync`) | A client-credentials token fetched via `ITokenClient` |
| Who's "behind" the call | Alice (or Bob) | Nobody — a service account |
| `sub`/`name`/`email` claims on the response | Present | **Absent** — there's no user in a client-credentials grant |

Click *Call the API (as me)* then *Call the API (as the service account)* on the secure
page and compare SampleApi's two responses directly — the claims table is visibly
different, not just a different label on the same result.

---

## Running it

Same four terminals as every phase since 4 (`ExternalIdp`, `IdentityServerHost`,
`MvcClient`, `SampleApi` — see `IdentityServerHost/README.md#running-it`). Then:

1. Browse to `https://localhost:5006`, click **Log in as Acme Corp**, sign in as
   `alice`/`alice`.
2. The secure page now shows **Tenant (from `ITenantContext`): Acme Corp (acme)** above
   the claims table — proof the middleware resolved it from the `tenant_id` claim, not
   just that the claim exists.
3. Click **Call the API (as me)** — same as before this port, now going through the
   config-driven `HttpClient` with retry/circuit-breaker attached.
4. Click **Call the API (as the service account)** — a brand new call, authenticated as
   `mvcclient-svc.acme` with no user involved at all. Compare its claims to step 3's.

Prefer not to click through a browser?
[`test-multitenancy-external-services.ps1`](../../../test-multitenancy-external-services.ps1)
(repo root) drives all of the above over raw HTTP, plus confirms Globex's login
independently exercises `mvcclient-svc.globex` with its own secret.

## What's deliberately not ported

- **SQL-backed tenant registry.** `Tenants.cs` here is a hard-coded dictionary; Apply's
  is an EF-backed `Tenants` table.
- **Hostname-based tenant resolution, and the real host-vs-claim cross-check.** See §1's
  `TenantResolutionMiddleware`/`RequireTenantAttribute` sections above for exactly what
  this means and what "Try it yourself" below would take to add.
- **EF Core global query filters** (`modelBuilder.Entity<T>().HasQueryFilter(x => x.TenantId == ...)`)
  — this sample has no database, so there's no per-tenant row-level isolation to
  demonstrate at all.
- **MassTransit tenant propagation** (`TenantFilter<T>` stamping a `tenant-key` message
  header so background consumers outside an HTTP request can rehydrate `ITenantContext`)
  — this sample has no message bus.
- **Per-tenant `IOptions<T>` caching machinery** (`MultiTenantOptionsFactory`/`Manager`/`Cache`,
  used in Apply to give the auth cookie a per-tenant name suffix, e.g. `"Apply.acme"`) —
  a genuinely clever generic pattern, but out of scope for a first step; this sample's
  cookie name is not tenant-suffixed.
- **Per-tenant CORS policy** — Apply's real `CorsPolicyProvider` actually unions CORS
  origins *across all tenants* rather than scoping per-request (a known simplification
  in the real system too, not something this port needed to fix or reproduce).
- **`WebApiConnector`/OIPA-style per-tenant integration config** — Apply's mechanism for
  client-specific (non-DIT) systems, entirely DB-driven with per-connector-type URL
  templates. No equivalent concept exists in this sample.
- **Autofac, distributed caching, `RefreshTokenRequestHostedService`, the
  iframe-aware OIDC handler** — infrastructure choices orthogonal to the multi-tenancy
  and external-services *patterns* this port focuses on.

## Try it yourself

1. **See the per-tenant Authority mechanism actually redirect somewhere else.** Add a
   fake entry to `IdentityGatewayApi:TenantUrls` in `appsettings.Development.json` —
   `{"acme": "http://localhost:9999"}` — restart MvcClient, and click *Log in as Acme
   Corp*. Watch the browser try to reach a URL that isn't serving anything, while
   *Log in as Globex Corporation* still works normally. This is the exact mechanism a
   real per-tenant IdentityServer deployment would use.
2. **Break a tenant's service-account access without touching the other tenant's.**
   Change `mvcclient-svc.acme`'s `clientSecret` in
   `IdentityServerHost/Configurations/IdentityServerConfig.json` (Phase 6 — this is no
   longer `Config.cs`) without updating
   `ExternalServicesApi:ServiceAccount:TenantSecrets:acme` to match, then re-run
   `ConfigIngestionTool` — Acme's *Call the API (as the service account)* now fails;
   Globex's keeps working.
3. **Add real hostname-based resolution**, the way Apply actually does it: this would
   mean adding a second resolution path to `TenantResolutionMiddleware` that runs
   *before* authentication (so it can influence which tenant the OIDC challenge targets,
   the way Apply's own middleware ordering does), reading `context.Request.Host` against
   a new `Multitenancy:TenantHostnames` config section, and then updating
   `RequireTenantAttribute` to compare that host-resolved tenant against the
   claim-resolved one — rejecting on mismatch, which is the real security boundary this
   sample's current filter can't check.
