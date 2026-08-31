# "Correlation failed" wiring up `entra-acme` — full root-cause writeup

This is the detailed postmortem behind adding a real Microsoft Entra ID provider
(`entra-acme`, see [`azure-entra-b2c-setup.md`](azure-entra-b2c-setup.md)) to this sample.
The short version lives in that doc's troubleshooting table; this doc is *why* each fix
was actually necessary, because the underlying cause changed twice as the fixes were
applied — a good case study in how "Correlation failed" can mean three different things
depending on what's actually blocking the cookie.

Three distinct bugs stacked here, found in this order:

1. A pre-existing bug in `ExternalController` that would have silently mislabeled every
   `entra-acme` login, unrelated to the correlation failure itself.
2. A corporate browser policy blocking cookies on plain HTTP, which broke federation the
   moment a *real* HTTPS identity provider (Entra) joined a sample that had only ever run
   over HTTP.
3. `SameSite=Lax` correctly excluding a cross-site `POST`, once (1) was fixed and Entra's
   actual response landed as a form post instead of a query-string redirect.

## Symptom

```
Microsoft.AspNetCore.Authentication.AuthenticationFailureException: An error was encountered while handling the remote login.
 ---> Microsoft.AspNetCore.Authentication.AuthenticationFailureException: Correlation failed.
```

Thrown from `RemoteAuthenticationHandler<TOptions>.HandleRequestAsync()` while
`/signin-oidc-entra` (the `entra-acme` `CallbackPath`) was being processed — i.e. Entra
had already redirected the browser back to this app with a response; the failure happens
*after* that, while ASP.NET Core tries to validate it.

## Bug 1 — `ExternalController` hardcoded the scheme name

Found by reading the code, not by reproducing anything: [`Controllers/ExternalController.cs`](../Controllers/ExternalController.cs)
built the local subject id and the `IdentityProvider` claim from the literal string
`"external-idp"`, regardless of which scheme actually authenticated the user:

```csharp
var localSubjectId = $"external:external-idp:{externalSubjectId}";
...
IdentityProvider = "external-idp"
```

This only ever mattered once a *second* provider (`entra-acme`) existed — with one
provider, the hardcoded literal happened to always be correct. It doesn't cause
"Correlation failed" (it runs after authentication succeeds), but it would have silently
mislabeled every `entra-acme` login as `external-idp` the moment the correlation issue
below was fixed, so it's fixed first.

**Fix:** thread the scheme name through `AuthenticationProperties.Items["scheme"]` the
same way `tenant` and `returnUrl` already round-trip, and read it back in `Callback()`
instead of the literal. See the `Challenge`/`Callback` actions in
`ExternalController.cs`.

## Bug 2 — corporate policy blocking cookies on `http://localhost`

### Diagnosis

Debug-level logging (`"Microsoft.AspNetCore.Authentication": "Debug"`) isolated the
exact failure:

```
Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler[15]
      '.AspNetCore.Correlation.0qNB6sFMsiOx322Jkmq1q5jvzmCsILdFPjb5hVjBIZk' cookie not found.
```

This is the key piece of evidence: the correlation ID embedded in the `state` parameter
*was* successfully decrypted (that's the only way the handler knows which cookie name to
look for) — the cookie itself simply was never sent back. That already rules out several
common causes:

- **Not** a Data Protection key-ring mismatch (state round-tripped fine).
- **Not** a scheme/config typo (same `AddOpenId` code path as the already-working
  `external-idp` provider — see `Configurations/Authentication/OpenId/OpenIdConnectAuthenticationExtensions.cs`).
- **Not** the classic `SameSite=None` gotcha this project's own README already documents
  for Phase 2 — that relaxation (`CorrelationCookie.SameSite = SameSiteMode.Lax`) was
  already applied generically to every provider.

Follow-up facts, gathered by asking rather than guessing further:

- Fails **every single time**, on the **first** attempt — not a retry/hot-reload/incognito
  flakiness pattern.
- Plain `http://localhost`, same browser tab throughout.
- Checking DevTools → Application → Cookies **immediately after clicking "Sign in with
  Microsoft"** (before Entra's login page even finished loading) showed **no correlation
  cookie at all** — not "set then lost on the way back," but never stored in the first
  place.
- The machine is a corporate-managed device (Equisoft-managed browser/Chrome-Edge with
  group policy).

That combination — cookie rejected at write time, deterministic, corporate-managed
browser — points at a managed browser policy that refuses to persist cookies on
non-HTTPS origins (policies like Chrome/Edge's `CookiesBlockedForUrls`,
`CookiesAllowedForUrls`, or `DefaultCookiesSetting` restricted to `https://*` are common
on managed fleets). A real HTTPS site (`login.microsoftonline.com`) never tripped this;
this sample's own plain-HTTP `http://localhost:5000` did, the moment it was in the same
cookie-setting position as any other origin.

### Fix — migrate to HTTPS

Rather than fight the browser policy, the correct fix is also just... correct: a sample
wiring up a real external IdP should run over HTTPS anyway. Concretely:

1. `dotnet dev-certs https --trust` — trusts the local ASP.NET Core dev certificate once,
   for every project in the solution.
2. Add an `https` launch profile to `IdentityServerHost/Properties/launchSettings.json`
   (`https://localhost:5001`).
3. Update the real Entra app registration's redirect URI to
   `https://localhost:5001/signin-oidc-entra` (byte-for-byte, same rule
   `azure-entra-b2c-setup.md` already calls out for `AADSTS50011`).

## Bug 2, continued — this cascades to every project that shares a cookie round-trip with IdentityServerHost

Moving *only* IdentityServerHost to HTTPS is not enough to leave the rest of the solution
working, because of a browser rule called **Schemeful Same-Site**: modern Chrome/Edge/Firefox
no longer consider `http://localhost:5000` and `https://localhost:5001` (or any other
`localhost` port) to be "the same site" the way the classic same-site definition (scheme
ignored, only registrable domain + ignoring port) used to. Scheme now has to match too.

This directly contradicts a reasoning comment already in this codebase — before this
change, it was correct:

> `MvcClient/Program.cs`: "Lax is the right relaxation here specifically because
> `localhost:5000` and `localhost:5002` are *same-site* (SameSite is defined by scheme +
> registrable domain, not port)."

That was true when both were `http`. The moment IdentityServerHost became `https` while
MvcClient stayed `http`, MvcClient's own login round-trip (MvcClient → IdentityServerHost
→ back to MvcClient) would start failing with the *exact same* "Correlation failed"
symptom as `entra-acme` did — same underlying mechanism, just one hop further out. The
same is true for `ExternalIdp`, which is IdentityServerHost's *own* federation partner for
the `external-idp` scheme (IdentityServerHost holds that correlation cookie, and needs a
consistent scheme across the round trip to `ExternalIdp` and back).

`SampleApi` and `ReactSpa` are exempt from this: `SampleApi` only validates bearer tokens
(no cookies involved at all), and `ReactSpa`'s OIDC client (`oidc-client-ts`, via
`react-oidc-context`) keeps its own flow state in browser storage, not cookies — so
neither needed a matching scheme, only an updated `authority`/URL pointing at
IdentityServerHost's new HTTPS address.

### Port map after the migration

Every project kept its original `http` profile in addition to gaining an `https` one
(nothing already working needed to break) — though note the port assignments below have
since been simplified to HTTPS-only per-project, see "Current state" below.

| Project | http (original) | https (added) | Why it needed to move |
|---|---|---|---|
| IdentityServerHost | 5000 | 5001 | Root cause: browser policy blocks cookies on http |
| MvcClient | 5002 | 5006 | Shares a correlation-cookie round trip with IdentityServerHost (its own OIDC login) |
| ExternalIdp | 5010 | 5011 | IdentityServerHost's own correlation cookie for the `external-idp` scheme needs a matching scheme on both legs of the round trip |
| SampleApi | 5003 | 5007 | No cookie involvement (bearer tokens only) — updated for consistency, not correctness |
| ReactSpa | 5173 (unchanged) | — | SPA flow state lives in browser storage, not cookies — only its `authority` URL needed updating |

### Files touched

- `IdentityServerHost/Properties/launchSettings.json` — added `https` profile
- `MvcClient/Properties/launchSettings.json` — added `https` profile
- `ExternalIdp/Properties/launchSettings.json` — added `https` profile
- `SampleApi/Properties/launchSettings.json` — added `https` profile
- `MvcClient/Program.cs` — `options.Authority` → `https://localhost:5001`
- `MvcClient/appsettings.Development.json` — `IdentityGatewayApi.Url`/`Issuer`,
  `ExternalServicesApi.ServiceAccount.TokenEndpoint`, and the `SampleApi` service
  definition's `BaseUri`, all repointed to their https counterparts
- `SampleApi/Program.cs` — `options.Authority` → `https://localhost:5001`
- `ExternalIdp/Config.cs` — the `mini-idg-host` client's `RedirectUris` → `https://localhost:5001/signin-external-idp`
- `IdentityServerHost/Config.cs` — the `mvcclient` client's `RedirectUris`/`PostLogoutRedirectUris` → `https://localhost:5006/...`
- `IdentityServerHost/appsettings.Development.json` — the `external-idp` provider's `Authority` → `https://localhost:5011`
- `ReactSpa/src/main.tsx` — `authority` → `https://localhost:5001`

### Current state

The project's own `http` launch profiles for IdentityServerHost, MvcClient, ExternalIdp,
and SampleApi were subsequently removed (each project now runs HTTPS-only) — a
deliberate simplification made after this migration, not something this doc's port table
should be read as overriding. Check each project's actual
`Properties/launchSettings.json` for the current, authoritative port.

## Bug 3 — `SameSite=Lax` correctly rejecting a cross-site `POST`

### Diagnosis

After Bug 2's fix, the correlation and nonce cookies *were* being stored (confirmed via
DevTools: `Secure` ✓, `SameSite=Lax`, present in the cookie jar) — and "Correlation
failed" still happened.

The distinction that matters here: IdentityServerHost ↔ MvcClient and IdentityServerHost
↔ ExternalIdp are same-site once both run HTTPS (same registrable domain, `localhost`,
matching scheme) — `SameSite=Lax` covers those hops regardless of HTTP method, because
they're not cross-site to begin with. **Entra is different: `login.microsoftonline.com`
is a genuinely different site from `localhost`, no scheme/port trick changes that.**

`SameSite=Lax` cookies are only included on cross-site requests when the request is a
**top-level GET navigation**. If Entra's authorization response comes back as a
`form_post` (an auto-submitting HTML form — a `POST`) instead of a query-string redirect
(a `GET`), the browser correctly withholds the Lax cookie on that `POST`, and the
correlation check fails again — a different root cause producing the identical exception
and log line as Bug 2.

`options.ResponseType = "code"` alone does not guarantee which `response_mode` Entra
picks; without an explicit `ResponseMode`, the OIDC handler omits the parameter entirely
and lets the identity provider choose.

### Fix

`Configurations/Authentication/OpenId/OpenIdConnectAuthenticationExtensions.cs` now
explicitly forces:

```csharp
options.ResponseMode = OpenIdConnectResponseMode.Query;
```

(from `Microsoft.IdentityModel.Protocols.OpenIdConnect` — not the
`Microsoft.AspNetCore.Authentication.OpenIdConnect` namespace already `using`'d in that
file, which doesn't define this type.)

This guarantees every provider's authorization response is a plain query-string
redirect — a `GET` — so `SameSite=Lax` reliably applies, without having to widen the
cookie policy to `SameSite=None` (which would in turn re-expose the sample to
third-party-cookie blocking, a *different* managed-browser restriction than the one Bug 2
fixed, and one that specifically targets `None` cookies). This also matches this file's
existing design philosophy of keeping every OIDC hop's parameters visible in the URL
(see the neighboring `PushedAuthorizationBehavior.Disable` comment) rather than relying
on an implicit, IdP-chosen default.

## Why three different fixes shared one symptom

"Correlation failed" only ever means one thing mechanically — the cookie named in the
decrypted `state` wasn't present on the request — but *why* the cookie wasn't present
differed each time:

| Attempt | Cookie present in browser? | Why the handler couldn't find it |
|---|---|---|
| Before Bug 2's fix | Never stored at all | Managed browser policy refused to store a cookie on `http://` |
| After Bug 2's fix, before Bug 3's fix | Stored, and visible in DevTools | Entra's `form_post` response is a cross-site `POST`; `SameSite=Lax` excludes it |
| After Bug 3's fix | Stored and sent | — |

The DevTools cookie-jar check (present vs. absent at each stage) was the deciding piece
of evidence both times — logging alone (which only reports "not found") can't
distinguish "never stored" from "stored but not sent."
