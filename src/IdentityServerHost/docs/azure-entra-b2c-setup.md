# Wiring a real Microsoft Entra ID or Azure AD B2C tenant into this sample

Phase 4 added federation to [`../../ExternalIdp`](../../ExternalIdp) — a toy Duende
IdentityServer that stands in for a real external identity provider. This doc is the
practice exercise the [lesson](../README.md#phase-4--external-identity-providers-per-tenant)
calls out explicitly: *"How would this look with a real Entra tenant instead of
ExternalIdp?"* It walks through registering a real Microsoft Entra ID tenant, and
separately a real Azure AD B2C tenant, and wiring either one in as a second (or
replacement) external scheme — same `ExternalController`, same `Tenants.cs` gating, same
pattern this repo already uses.

**Read this before you start:** Microsoft Entra ID and Azure AD B2C solve different
problems. Pick based on who's signing in, not on which one sounds more modern.

| | Microsoft Entra ID (direct) | Azure AD B2C |
|---|---|---|
| Who signs in | People in a real organization's work/school directory | Consumers/customers with email, social logins, self-service sign-up |
| Identity model | One directory you (or a partner) already run | A separate, dedicated customer-identity tenant you create |
| Password reset, sign-up UI | Not this product's job — assumes the org's admin handles it | Built in (via "user flows") |
| Status as of this writing | Fully supported, no deprecation | **Closed to new customers since May 1, 2025** — see callout below |

> **Azure AD B2C is in maintenance mode for new customers.** Microsoft stopped selling
> B2C to new customers on **May 1, 2025** — new subscriptions cannot create new B2C
> tenants. If you already have a B2C tenant from before that date, it continues to work
> and is supported until at least **May 2030** (B2C P2 specifically is discontinued
> March 15, 2026 — new tenants after that can only use P1). For any **new** consumer-identity
> project, Microsoft's current recommendation is
> [Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/customers/overview-customers-ciam)
> instead. The B2C section below is written for readers who already have a tenant to
> point this sample at; if you don't, skip to that link instead.

This mirrors exactly what *Lesson 53 — Entra ID Without B2C* of this course already found working
against the real IdG: Entra ID direct is "the simpler configuration," and B2C is the one
you reach for only when you specifically need consumer sign-up/password-reset UX (or
already have a B2C tenant in production).

---

## Option A — Microsoft Entra ID (direct), recommended

### 1. Register the application in Entra

1. Go to the [Microsoft Entra admin center](https://entra.microsoft.com) → **App
   registrations** → **New registration**.
2. **Name**: anything (e.g. `mini-idg-entra`) — cosmetic only.
3. **Supported account types**: *Accounts in this organizational directory only*
   (single tenant) for a first pass — that's what this sample's other client
   registrations assume.
4. **Redirect URI**: platform **Web**, value
   `http://localhost:5000/signin-oidc-entra`. This must match the `CallbackPath` you
   configure in step 3 below **byte for byte** — scheme, host, port, path, no trailing
   slash difference. This is the single most common way this integration fails
   (`AADSTS50011` if it doesn't match).
5. On the **Overview** blade, copy:
   - **Application (client) ID** → this becomes `ClientId` below.
   - **Directory (tenant) ID** → this becomes the `{tenantId}` in the Authority URL below.
6. **Certificates & secrets** → **New client secret** → copy the **Value** column, not
   the *Secret ID* (they look similar; only *Value* is the actual secret — you can't
   retrieve it again after leaving the page).
7. **API permissions**: the default delegated `User.Read` is enough — this sample never
   calls Microsoft Graph.

### 2. Keep the secret out of source control

```powershell
cd src/IdentityServerHost
dotnet user-secrets init
dotnet user-secrets set "ExternalProviders:OpenId:1:ClientSecret" "<the secret VALUE from step 1.6>"
```

`user-secrets` overlays the same configuration tree `appsettings.*.json` populates —
`ExternalProviders:OpenId:1:ClientSecret` means "the `ClientSecret` field of the
*second* entry (index `1`) in the `OpenId` array," matching whatever position you give
it in step 3 below. This is the config-driven equivalent of what the "Wiring a real
external provider" section of
[`external-providers-configuration.md`](external-providers-configuration.md) calls
"where secrets go" — the same rule applies here as for `external-idp`'s (throwaway,
fine-to-commit) secret, just for a real one now.

### 3. Add it to `appsettings.Development.json`

No `Program.cs` change needed — a second entry in the `OpenId` array is all a new
provider is:

```json
"ExternalProviders": {
  "OpenId": [
    { "Name": "external-idp", "...": "...existing ExternalIdp entry..." },
    {
      "Name": "entra-acme",
      "DisplayName": "Sign in with Microsoft",
      "EcosystemTenant": "acme",
      "Authority": "https://login.microsoftonline.com/<tenant-id-from-step-1.5>/v2.0",
      "ClientId": "<application-client-id-from-step-1.5>",
      "ClientSecret": "<set via user-secrets, see step 2 — leave this out entirely, don't put a placeholder>",
      "CallbackPath": "/signin-oidc-entra",
      "Scopes": ["email"]
    }
  ]
}
```

The `Authority` above (`.../v2.0`) is the recommended shape — it gives you
`preferred_username` and other v2.0-only claims, and Microsoft's own guidance is that
new integrations should target v2.0. The older `{instance}/{tenant}` shape (no
`/v2.0`) resolves the v1.0 discovery document instead — see *Lesson 53* for why the
real IdG's two Entra provider types (`azuread` vs. `openidconnect`) differ on exactly
this point; this sample's single `OpenId` type can point at either shape, you're just
choosing which one to write into `Authority`.

`EcosystemTenant: "acme"` is what makes this show up on Acme's login page:
`Configurations/Authentication/Helpers/AuthenticationHelper.cs` filters the whole
provider list by this field, so Acme's login page automatically offers a *choice* of
two providers with **no code change anywhere** — see
[`external-providers-configuration.md`](external-providers-configuration.md)'s "How
tenant gating works now" for the mechanics.

### 5. What you'll see when it works

Sign in, land back on the secure page or claims table, and the `sub` claim will be a
subject id scoped to `(scheme, Entra's own sub)` — same shape as Carol's
`external:external-idp:ext-1`, just `external:entra-acme:<entra sub>`. If you want to
confirm you're really talking to Entra and not a cached ExternalIdp session, check the
`idp` claim — it'll read `entra-acme`.

### Troubleshooting

| Symptom | Cause |
|---|---|
| `AADSTS90002: Tenant '...' not found` | Still the placeholder tenant ID, or a typo in the GUID |
| `AADSTS50011: The redirect URI specified in the request does not match...` | `CallbackPath` doesn't byte-for-byte match the redirect URI registered in step 1.4 |
| `AADSTS7000215: Invalid client secret provided` | You pasted the **Secret ID** instead of the **Value**, or the secret expired |
| Claim missing, no error at all | Not requested via `Scope`, or `MapInboundClaims` behavior isn't what you expected — this fails silently, always check both |
| Works once, then "correlation failed" on the next attempt | Same cookie relaxation this sample already needs for `external-idp` — confirm `CorrelationCookie`/`NonceCookie` `SameSite = Lax` is set on the new registration too |
| "Correlation failed" every time, first attempt, cookie never appears in DevTools | Deeper than the row above — see [`correlation-failed-troubleshooting.md`](correlation-failed-troubleshooting.md) for the full investigation (a managed-browser policy blocking cookies over plain HTTP, the HTTPS migration it forced across the whole solution, and a follow-up `SameSite=Lax` vs. cross-site `form_post` issue) |

---

## Option B — Azure AD B2C

Only do this if you already have an existing B2C tenant (see the callout at the top of
this doc) — for a brand-new setup, use
[Microsoft Entra External ID](https://learn.microsoft.com/en-us/entra/external-id/customers/overview-customers-ciam)
instead; its app-registration and user-flow steps are conceptually the same shape as
what follows, just under a different, actively-developed product.

### 1. Create (or open) the B2C tenant

Azure Portal → your existing Azure AD B2C tenant (**Azure AD B2C** resource) → make sure
you're operating **inside** the B2C tenant's directory (switch directories via the
account picture menu if needed — this is the most common early mistake, registering the
app in your main Azure subscription's directory instead of the B2C tenant).

### 2. Create a user flow

**Azure AD B2C** → **User flows** → **New user flow** → **Sign up and sign in**
(Recommended version) → name it, e.g. `B2C_1_signupsignin`. This is what actually
presents the sign-up/sign-in UI — Entra ID direct has no equivalent because it assumes
the org's own directory already has these people.

### 3. Register the application

**App registrations** → **New registration**, inside the B2C tenant:
- **Redirect URI**: Web, `http://localhost:5000/signin-oidc-b2c`.
- Copy **Application (client) ID**.
- **Certificates & secrets** → new secret → copy the **Value**.
- Note your B2C tenant's domain, e.g. `yourtenant.onmicrosoft.com`.

### 4. Add it to `appsettings.Development.json`

```json
{
  "Name": "b2c-acme",
  "DisplayName": "Sign in with Acme (B2C)",
  "EcosystemTenant": "acme",
  "Authority": "https://yourtenant.b2clogin.com/yourtenant.onmicrosoft.com/B2C_1_signupsignin/v2.0",
  "ClientId": "<application-client-id-from-step-3>",
  "CallbackPath": "/signin-oidc-b2c"
}
```

(`ClientSecret` set via `dotnet user-secrets`, same as step 2 in Option A — index this
one by wherever it lands in the `OpenId` array.)

B2C's authority is policy-scoped, unlike Entra ID direct: the user flow name is part of
the URL. Get the exact value from the user flow's own **Run user flow** button in the
portal — don't hand-assemble it from memory.

### 5. The identifier caveat that matters most

B2C sits **between** you and whatever the person actually authenticated with — even if
that's their own Entra ID work account, federated through B2C. The `oid` claim you get
back from B2C is a **B2C-local** identifier, not the same value you'd get talking to the
underlying Entra tenant directly. This sample's `ExternalController.Callback` doesn't
need to care (it keys on `(scheme, sub)`, same as it does for `external-idp` and for
Option A above) — but if you were ever migrating users from B2C to direct Entra ID
later, the identifiers wouldn't match up, and every user would effectively be
re-provisioned. This is exactly what *Lesson 53* found against the real IdG, and it's
the reason that lesson's verdict is "prefer direct Entra ID unless you specifically need
what B2C adds."

---

## How this differs from the real IdG (read before assuming this generalizes)

This sample now loads external providers from `appsettings.*.json` and registers one
`AddOpenIdConnect(...)` call per entry — see
[`external-providers-configuration.md`](external-providers-configuration.md) for the
config shape and code path. That much matches the real IdG's *file-based* provider path
(`externalproviderssettings.json` → `AddExternalProvidersFromFile`) closely. It still
diverges in real-world ways this sample's toy `external-idp` never surfaced, and that
adding Entra/B2C for real would:

- **Only one provider type exists here (`OpenId`).** The real IdG gives Entra ID
  (`azuread`) and Azure AD B2C (`azureadb2c`) their *own* provider types, each with
  protocol-specific behavior the generic OIDC type doesn't have — see the "Claim
  collisions" point below for a concrete example. This sample's single `OpenId` type
  works for either (as both Options above show), but doesn't replicate that extra
  behavior. See `external-providers-configuration.md`'s "What's still hardcoded" for
  what porting `AzureAd`/`AzureAdB2C` as first-class types would take.
- **Claim collisions.** The real IdG's `azuread` provider type strips a duplicate
  `ClaimTypes.Name` claim (which holds the *email*, not the display name) — a
  consequence of that provider type using inbound claim mapping, which every OIDC
  handler in this repo explicitly disables (`MapInboundClaims = false`). You likely
  won't hit this here, but you should know why, not just that it doesn't happen.
- **`FederatedConfiguration` and `ClaimMappings` are modeled but not consumed.** Both
  exist as settings (`external-providers-configuration.md` explains why), but
  `ExternalController.Callback()` doesn't act on either yet — needed when a broker
  (like B2C) hides the real durable identifier, or when the ecosystem keys off a legacy
  claim name a provider doesn't use natively.
- **File-based only, eagerly bound at startup — no database.** The real IdG's DB-backed
  path (providers as rows, loaded lazily via Duende's dynamic-provider feature so a new
  provider needs no app restart) needs a configuration store first — that's Phase 5.
- **Redirect URI is computed, not free-form**, for the real IdG's *database-backed*
  providers specifically: `{pathPrefix}/{scheme}/signin`. Renaming a scheme there breaks
  the provider registration in Entra/B2C. This sample's `CallbackPath` (like the real
  IdG's own *file-based* providers) is just whatever string you write, so this
  particular trap only applies to the DB path either way.

## References

- [Microsoft Entra — ID token claims reference](https://learn.microsoft.com/en-us/entra/identity-platform/id-token-claims-reference) — read *"Use claims to reliably identify a user"*; the authoritative statement that `oid` is immutable per directory while `sub` is pairwise per application.
- [ASP.NET Core external authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/) — the handler mechanics this sample's `AddOpenIdConnect` calls build on.
- [Microsoft Entra External ID overview](https://learn.microsoft.com/en-us/entra/external-id/customers/overview-customers-ciam) — the modern replacement for new consumer-identity (B2C-shaped) projects.
- [Azure AD B2C FAQ](https://learn.microsoft.com/en-us/azure/active-directory-b2c/faq) — current support/retirement timeline for existing B2C tenants.
- *Lesson 53 — Entra ID Without B2C* and *Lesson 56 — Wire Entra ID Into Your
  IdentityServer* (OAuth & Identity Course, Module 6/7) and their companion
  *Provider Config Cheat Sheet* — written against the real IdG, these cover the
  JSON/DB-driven provider config, `FederatedConfiguration`, and the full claim-rename
  cascade this doc only summarizes. Not included in this repo; ask whoever shared the
  mini-idg lessons with you for a copy if you don't already have access.
