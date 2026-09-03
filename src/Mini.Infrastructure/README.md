# Mini.Infrastructure

The shared plumbing three projects in this repo actually consume. Introduced in Phase 10 by
**extracting code that already existed in more than one place**, not by designing a library
up front.

One csproj, organised by folder. It gets split only when a phase genuinely forces it — and
if that happens, that's a "things that broke" entry, not a silent refactor.

```
Identity/          who is calling — claims-only, no browser assumed
ExternalServices/  calling another service — tokens, service registry
Http/              resilience policies for outbound calls
```

## What this is *not*

This is not a from-scratch rebuild of the DIT infrastructure libraries. That course already
exists and is finished, at `C:\MyWork\MyLearning\EqusoftInfra` — nine series that teach
`DIT.Identity`, `DIT.HTTP`, `DIT.Connectors`, `DIT.Auth`, `DIT.Persistence`, `DIT.WebApi`
and more by having you rebuild each one as `MyCompany.*`.

The two have different jobs, and keeping them apart is deliberate:

| | This repo | EqusoftInfra |
| --- | --- | --- |
| Question it answers | Which service calls which, carrying what token, and what breaks | What's inside `AddConnectors()` / `AddIdentityContext()` |
| Unit of work | A numbered **Phase** producing a runnable slice of a system | A **Series** producing a mini-library |
| Audience | Anyone who needs to reason about DIT identity end to end | New junior devs learning library design |

So when a file here needs to explain *why the real DIT library is built the way it is*, it
links to the relevant EqusoftInfra lesson instead of re-explaining it. Relevant lessons:
Series 2 (`DIT.Identity` — the real `IIdentityContext` this folder's version is cut down
from), Series 9 (`DIT.HTTP` — the config-driven policies `Http/ResiliencePolicies.cs`
hardcodes), Series 5 (`DIT.Auth`), Series 4 (`DIT.Connectors`).

## What was deliberately NOT extracted

Phase 10 set out to collapse what looked like three copies of the same tenant plumbing.
Reading them side by side showed that most of it isn't duplication at all. This section is
the record of that, because "why didn't you just share this?" is the first question anyone
will ask.

### The two `TenantContext`s stay separate

`IdentityServerHost/TenantContext.cs` and `MvcClient/Infrastructure/MultiTenant/TenantContext.cs`
have similar names and almost nothing else in common:

| | IdentityServerHost | MvcClient |
| --- | --- | --- |
| Resolved from | `acr_values=tenant:<name>` on the query string | the `tenant_id` **claim** |
| Resolved when | **before** anyone has authenticated | **after** authentication; needs `User.Identity.IsAuthenticated` |
| What it means | which tenant this login *attempt* is for — an intent | which tenant the signed-in user *is in* — an established fact |
| No tenant means | normal; a login with no tenant hint still succeeds (`test-phase3.ps1` §4 asserts exactly this) | a failure; `RequireTenantAttribute` returns 401 |
| Shape | mutable `TenantKey` + `DisplayName` | one-time `SetTenant(Tenant)`, `Tenant` get-only |

They are not one abstraction with two resolvers. They sit on opposite sides of the
authentication boundary and disagree about whether "no tenant" is an error. Merging them
would mean inventing a type whose invariants neither caller actually holds.

`CONTEXT.md` asserted this before it was tested; Phase 10 tested it, and the assertion
survived.

### The two `Tenants` registries stay separate — and that's the point

`MvcClient/Infrastructure/MultiTenant/Tenants.cs` already explains why, and it was right:

> deliberately a SEPARATE registry from IdentityServerHost's own `Tenants.cs`, not a shared
> reference to it. That duplication [...] is the point, not an oversight: in the real system,
> Apply's Tenants table and the IdG's tenant registry are two independent stores, kept in
> sync by an ops process, not by sharing code — a mismatch between them [...] is a real,
> meaningful failure mode this sample can now actually reproduce.

Sharing them would make a real production failure mode unrepresentable. They aren't even
the same shape: IdentityServerHost maps key → display-name string and owns
`ResolveTenantKey` (`acr_values` parsing); MvcClient maps key → a `Tenant` object and owns
`Find`.

**And the drift is already real.** Phase 9 added `initech` to IdentityServerHost's registry
and to `ExternalServicesStub`'s. It is *not* in MvcClient's — so an Initech user who
completes login at IdentityServerHost and lands on MvcClient resolves to no tenant and gets
a 401 from `RequireTenantAttribute`. That is not a bug to fix in this phase; it is exactly
the ops-drift failure the comment above predicted, now reproducible in three files. See
IdentityServerHost's README, Phase 10 section, for how to watch it happen.

### `IdentityGatewayConfiguration` stays in MvcClient

It describes *this app's* relationship to the identity gateway (its own client id, secret,
and per-tenant IdG URLs). Nothing else in the repo has that relationship. A config class
used by one project is not shared code.

### IdentityServerHost's `ExternalServicesOptions` stays put, for now

It's bound from a section with the same name as MvcClient's (`ExternalServicesApi`) but is a
genuinely different shape: a self-issued JWT with a `client_id` claim and no secret, versus
real client-credentials against `/connect/token` with a per-tenant secret. Phase 11 converges
IdentityServerHost onto the service-account path, and *that* is the phase where this either
moves here or is deleted.
