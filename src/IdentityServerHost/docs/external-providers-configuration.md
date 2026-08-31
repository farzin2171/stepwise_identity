# External providers — configuration reference

This is the first step of porting how `Applications.IdentityGateway` (the real IdG)
configures external identity providers into this sample, replacing Phase 4's original
hardcoded `.AddOpenIdConnect("external-idp", options => { ... })` block in `Program.cs`
with a config-driven system shaped the same way the real one is. It's an intentionally
small first step — one provider type (`OpenId`), no database — with the seams left
visible for where `AzureAd`, `AzureAdB2C`, and persistence would each slot in later.

## Why config-driven at all?

Before this step, adding a second external provider meant writing a second
`.AddOpenIdConnect(...)` C# block and rebuilding. The real IdG's whole design point is
that provisioning a client's SSO integration is an **ops/config change**, not a code
change — a new tenant's Entra tenant ID and secret go into a config file (or a database
row), not a pull request. This step ports that shape, not (yet) the database part of it.

## The `ExternalProviders` config section

Lives in `appsettings.Development.json` (dev-only — see "Where secrets go," below):

```json
"ExternalProviders": {
  "OpenId": [
    {
      "Name": "external-idp",
      "DisplayName": "ExternalIdp (partner SSO)",
      "EcosystemTenant": "acme",
      "Authority": "https://localhost:5011",
      "ClientId": "mini-idg-host",
      "ClientSecret": "external-secret",
      "CallbackPath": "/signin-external-idp"
    }
  ]
}
```

This is exactly `../ExternalIdp`'s registration, moved out of `Program.cs` and into
config — nothing about the login page or `ExternalController` changed to make this work.

### Field reference

| Field | Required | Meaning |
|---|---|---|
| `Name` | yes | The authentication scheme name — what `Challenge(props, scheme)` and `/External/Challenge?scheme=...` pass around. Must be unique across every provider you configure. |
| `DisplayName` | yes | What the login page's button says: "Sign in with `{DisplayName}`". |
| `EcosystemTenant` | yes | Which tenant (`Tenants.DisplayNames` key — `acme`, `globex`) this provider belongs to. This is what `AuthenticationHelper.GetAllAvailableIdentityProviders(tenantKey)` filters on — see "How tenant gating works now," below. |
| `Authority` | yes | The OIDC issuer to trust. For `ExternalIdp` this is `https://localhost:5011`; for a real Entra ID tenant it looks like `https://login.microsoftonline.com/{tenantId}/v2.0` — see [`azure-entra-b2c-setup.md`](azure-entra-b2c-setup.md). |
| `ClientId` | yes | The client ID this app registered as, on the provider's side. |
| `ClientSecret` | yes (for a confidential client) | See "Where secrets go," below — never commit a real one. |
| `CallbackPath` | yes | Must exactly match a redirect URI registered with the provider — see the callout below. |
| `Scopes` | no (default `[]`) | Extra scopes appended to `openid`/`profile`, which every provider gets automatically and can't lose by configuring this. |
| `GetClaimsFromUserInfoEndpoint` | no (default `true`) | Whether to call the provider's `/userinfo` after token exchange — see MvcClient's README for why the code flow's ID token alone often isn't enough. |
| `FederatedConfiguration`, `ClaimMappings` | no | Modeled (see below), **not yet consumed anywhere**. |

> **`CallbackPath` has to byte-for-byte match what's registered with the provider.**
> `AADSTS50011: The redirect URI specified in the request does not match...` is what a
> real Entra ID tenant tells you when it doesn't — scheme, host, port, and path all have
> to agree exactly, including trailing slashes.

> **Every provider you register here gets Pushed Authorization Requests (PAR)
> automatically disabled** (`OpenIdConnectAuthenticationExtensions.cs`:
> `PushedAuthorizationBehavior.Disable`). Any Duende IdentityServer you point this at —
> `ExternalIdp` included — advertises a `pushed_authorization_request_endpoint`, and the
> OIDC handler's default is to use PAR whenever a server offers it: the real authorize
> parameters get POSTed server-side and the browser only ever sees
> `?request_uri=urn:...&client_id=...`. Functionally fine, but it hides every parameter
> this sample deliberately keeps visible — same reasoning, same fix, as the PAR gotcha
> already documented on the MvcClient↔IdentityServerHost hop (see MvcClient's README).

## How tenant gating works now

Phase 4 originally hardcoded which schemes each tenant could see:

```csharp
// gone — this was Tenants.AllowedExternalSchemes
["acme"] = ["external-idp"],
["globex"] = []
```

That's a second, parallel mapping that has nothing forcing it to stay in sync with the
actual list of registered providers — exactly the kind of drift the real IdG's design
avoids. Now, tenant gating is a property **of the provider itself**
(`EcosystemTenant`), and `AccountController` asks for "every provider tagged for this
tenant" instead of maintaining its own list:

```csharp
// Configurations/Authentication/Helpers/AuthenticationHelper.cs
public IEnumerable<IAuthenticationOptions> GetAllAvailableIdentityProviders(string? tenantKey)
{
    if (tenantKey is null) return [];
    return options.Value.OpenId.Where(provider => provider.EcosystemTenant == tenantKey);
}
```

Add a second provider tagged `"EcosystemTenant": "acme"` in config, and Acme's login
page offers a choice, with **no code change anywhere** — that's the whole point of this
refactor, and exactly the "add a second external scheme" exercise Phase 4's README
already suggested trying.

## Where secrets go

`appsettings.Development.json` is fine for `external-idp`'s throwaway secret — it's a
teaching sample and `ExternalIdp` isn't a real service. For a real provider (see
[`azure-entra-b2c-setup.md`](azure-entra-b2c-setup.md)), the client secret belongs in
`dotnet user-secrets` locally, and a real secret store (Key Vault, etc.) in any shared
environment — never in a file meant to be committed. This mirrors the real IdG's own
split: `identityProviders.json` (checked in, used to seed a database) never contains a
real secret value either; those are injected separately at deploy time.

## What's modeled but not yet wired: `FederatedConfiguration` and `ClaimMappings`

Both exist on `IAuthenticationOptions` — and both are currently no-ops. They're modeled
now, ahead of being used, specifically so the settings shape matches the real IdG and
the gap is visible rather than silently absent:

- **`ClaimMappings`** (`IDictionary<string, string>`) — the real IdG applies this to
  every incoming claim from the external provider, *before* anything else touches it
  (`ClaimsExtensions.ApplyClaimMappings`, called at the top of
  `ExternalController.FindUserFromExternalProviderAsync`). It's how a provider that
  emits some non-standard claim name (e.g. `extn.OIPAClientID`) gets it renamed to
  something the rest of the pipeline recognizes (e.g. `externalobjectidentifier`),
  without needing a whole new provider type. Wiring this into
  `ExternalController.Callback()` here would mean applying `ClaimMappings` to
  `result.Principal.Claims` right after `HttpContext.AuthenticateAsync(...)`, before the
  `name`/`sub` extraction that already happens there.
- **`FederatedConfiguration`** (`Enabled`, `TokenName`, `ObjectIdClaimName`) — for the
  case where the provider you're federating to is itself a *broker* in front of another
  IdP (Azure AD B2C in front of a corporate Entra tenant is the classic case — see
  [`azure-entra-b2c-setup.md`](azure-entra-b2c-setup.md)'s B2C section). The identifier
  you actually want isn't on the top-level token the broker hands you; it's nested
  inside an embedded token the broker passes through. `TokenName` says which claim holds
  that nested token; `ObjectIdClaimName` says which claim inside *that* token is the
  real durable id. `ExternalIdp` is a single-hop provider — there's no nested token to
  unwrap — so this sample has never needed it. Wiring it in would mean: in
  `ExternalController.Callback()`, if `FederatedConfiguration.Enabled`, look up
  `result.Principal.FindFirst(TokenName)`, parse its value as a JWT, and pull
  `ObjectIdClaimName` out of *that* token's claims instead of the outer principal's.

Neither is a large amount of code to add — they're left out of this first step because
nothing in this sample's provider list needs them yet, not because they're hard.

## What's still hardcoded (next steps, not done here)

- **Only the `OpenId` provider type exists.** Adding `AzureAd`/`AzureAdB2C` support
  means: a new options class under `Configurations/Authentication/AzureAd/` (or
  `AzureAdB2C/`) extending the same `BaseAuthenticationOptions` shape, a matching
  `Add*()` extension following `OpenIdConnectAuthenticationExtensions`'s pattern, a new
  list on `ExternalProvidersOptions`, and a loop over it in
  `AddExternalProvidersFromFile`. [`azure-entra-b2c-setup.md`](azure-entra-b2c-setup.md)
  already shows what the resulting `OpenIdConnectOptions` configuration needs to look
  like for both Entra ID and B2C — porting it into this shape is mechanical, not novel.
- **File-based only, eagerly bound at startup.** The real IdG's other path — providers
  stored in a database, loaded lazily via Duende's dynamic-provider feature
  (`AddDynamicIdentityProviders()`), so adding a provider is a database write with no
  app restart — needs a configuration store first. That's Phase 5.
- **No `Priority` tiebreak, no multi-provider-per-tenant ordering logic.** With at most
  one provider per tenant so far, there's nothing to order.
