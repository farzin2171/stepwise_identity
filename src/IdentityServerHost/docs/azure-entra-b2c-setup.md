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
dotnet user-secrets set "EntraId:ClientSecret" "<the secret VALUE from step 1.6>"
```

Then read it in `Program.cs` via `builder.Configuration["EntraId:ClientSecret"]` instead
of a hardcoded string — the way `external-secret` is hardcoded in this sample's
`Program.cs` is fine for a **toy** IdP with a throwaway secret; it is not fine for a
real Entra app registration's secret.

### 3. Wire it into `Program.cs`

Add a second `.AddOpenIdConnect(...)` call, right alongside the existing `"external-idp"`
one — same builder, same `AddAuthentication()` chain:

```csharp
builder.Services.AddAuthentication()
       .AddOpenIdConnect("external-idp", options => { /* ...existing ExternalIdp config... */ })
       .AddOpenIdConnect("entra-acme", options =>
       {
           options.SignInScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;

           // Recommended over the "azuread" style Authority ({instance}/{tenant}, no
           // /v2.0) that pins you to the v1.0 endpoint — see Lesson 53. The v2.0
           // discovery document gives you preferred_username and is what Microsoft
           // recommends for new integrations.
           options.Authority = $"https://login.microsoftonline.com/{builder.Configuration["EntraId:TenantId"]}/v2.0";
           options.ClientId = builder.Configuration["EntraId:ClientId"]!;
           options.ClientSecret = builder.Configuration["EntraId:ClientSecret"];
           options.ResponseType = "code";
           options.UsePkce = true;
           options.CallbackPath = "/signin-oidc-entra";

           options.SaveTokens = true; // keeps Entra's tokens in the external cookie, same as MvcClient's pattern

           // Scope constructor already adds "openid" and "profile" — profile is required
           // to receive "oid" at all (Microsoft's own claims reference says so explicitly).
           options.Scope.Add("email");

           options.MapInboundClaims = false; // keep claim types exactly as Entra sends them —
                                              // this is also why you likely WON'T hit the classic
                                              // "duplicate ClaimTypes.Name holds the email" bug the
                                              // real IdG's azuread provider works around (Lesson 56):
                                              // that bug is specific to having inbound claim mapping
                                              // turned ON, which every OIDC handler in this repo disables.

           options.CorrelationCookie.SameSite = SameSiteMode.Lax;
           options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
           options.NonceCookie.SameSite = SameSiteMode.Lax;
           options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
       });
```

### 4. Gate it by tenant, same as `external-idp`

```csharp
// Tenants.cs
public static IReadOnlyDictionary<string, string[]> AllowedExternalSchemes => new Dictionary<string, string[]>
{
    ["acme"] = ["external-idp", "entra-acme"],   // Acme now offers a choice of two
    ["globex"] = []
};

public static IReadOnlyDictionary<string, string> SchemeDisplayNames => new Dictionary<string, string>
{
    ["external-idp"] = "ExternalIdp (partner SSO)",
    ["entra-acme"] = "Sign in with Microsoft"
};
```

Nothing else changes — `ExternalController.Challenge`/`Callback` are already
scheme-agnostic (the `scheme` route parameter is just whatever string you pass), and
`Login.cshtml` already loops over `Model.ExternalSchemes` and renders one button per
entry. This is the exact "add a second external scheme" exercise the Phase 4 lesson
suggests trying.

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

### 4. Wire it into `Program.cs`

```csharp
.AddOpenIdConnect("b2c-acme", options =>
{
    options.SignInScheme = IdentityServerConstants.ExternalCookieAuthenticationScheme;

    // B2C's authority is policy-scoped, unlike Entra ID direct: the user flow name is
    // part of the URL. Get this from the user flow's own "Run user flow" button in the
    // portal, which shows you the exact endpoint — don't hand-assemble it from memory.
    options.Authority = "https://yourtenant.b2clogin.com/yourtenant.onmicrosoft.com/B2C_1_signupsignin/v2.0";
    options.ClientId = builder.Configuration["B2C:ClientId"]!;
    options.ClientSecret = builder.Configuration["B2C:ClientSecret"];
    options.ResponseType = "code";
    options.UsePkce = true;
    options.CallbackPath = "/signin-oidc-b2c";
    options.SaveTokens = true;
    options.MapInboundClaims = false;

    options.CorrelationCookie.SameSite = SameSiteMode.Lax;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.NonceCookie.SameSite = SameSiteMode.Lax;
    options.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
```

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

This sample wires each external provider as a hardcoded `.AddOpenIdConnect(...)` call in
`Program.cs`, compiled in. The real IdG instead loads an arbitrary list of providers from
JSON config (or a database) at startup and calls `AddOpenIdConnect` once per entry in a
loop — adding a provider there is a config change, not a code change, and (for
DB-backed providers) requires an app restart to pick up. It also has real-world
complications this sample's toy `external-idp` never surfaces:

- **Claim collisions.** The real IdG's `azuread` provider type strips a duplicate
  `ClaimTypes.Name` claim (which holds the *email*, not the display name) — a
  consequence of that provider type using inbound claim mapping, which every OIDC
  handler in this repo explicitly disables (`MapInboundClaims = false`). You likely
  won't hit this here, but you should know why, not just that it doesn't happen.
- **`FederatedConfiguration`.** Needed when a broker (like B2C) hides the real durable
  identifier, or when the ecosystem keys off a legacy id from *before* migrating to
  Entra. Not implemented in this sample at all — see Lessons 49–58 for the real
  mechanics.
- **Redirect URI is computed, not free-form**, for database-backed real-IdG providers:
  `{pathPrefix}/{scheme}/signin`. Renaming a scheme there breaks the provider
  registration in Entra/B2C. This sample's `CallbackPath` is just whatever string you
  write, so this particular trap doesn't exist here — one more way this sample is
  simpler than the real thing, not a simplification you should assume carries over.

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
