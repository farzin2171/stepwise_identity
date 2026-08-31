# ExternalIdp — a stand-in for a real external identity provider

A second, completely independent Duende IdentityServer. It exists to give
[`../IdentityServerHost`](../IdentityServerHost) something real to federate to in Phase
4 — a partner's identity provider, a corporate Entra tenant, whatever a real deployment
would call an "external IdP." **This project knows nothing about mini-idg's tenants, or
even that it's part of a bigger sample.** From its point of view, IdentityServerHost is
just another OIDC relying party, registered as an ordinary client — the same way any
other web app would be.

```
IdentityServerHost (:5001)  —Authorization Code + PKCE→  ExternalIdp (:5011)
        (as a CLIENT of this server, using ClientId "mini-idg-host")
```

## Why a whole second IdentityServer, not just a mock

Federation only teaches something real if the "external" half is genuinely external —
its own process, its own sessions, its own opinions about who's logged in. A mocked-out
HTTP response would skip past everything Phase 4 is actually about: a real
`response_mode=form_post` round trip, a real second login page, a real authorization
code that IdentityServerHost has to redeem for a token it didn't mint itself.

## What's in this project

Structurally identical to [`../IdentityServerHost`](../IdentityServerHost)'s Phase 1 +
Phase 2 shape — same `AddIdentityServer()`/`UseIdentityServer()` pair, same
`AccountController`/`Login.cshtml` pattern for its own local login. The only interesting
part is `Config.cs`:

```csharp
new Client
{
    ClientId = "mini-idg-host",
    ClientSecrets = { new Secret("external-secret".Sha256()) },

    AllowedGrantTypes = GrantTypes.Code,
    RequirePkce = true,
    RequireConsent = false,

    RedirectUris = { "https://localhost:5001/signin-external-idp" },

    AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile }
}
```

Same shape as `mvcclient` and `reactspa`'s registrations in
`IdentityServerHost/Configurations/IdentityServerConfig.json` (Phase 6) — except here,
**IdentityServerHost is the client being registered.** Same
grant type, same PKCE requirement, same shape. The only test user is Carol
(`carol`/`carol`), who exists *only here* — the entire point of federation is that the
relying party (IdentityServerHost) doesn't maintain its own password for her.

Also structurally identical, and easy to forget: `Program.cs`'s cookie relaxation.
**This project is its own Duende IdentityServer, so it has the exact same
`SameSite=None`-without-`Secure` cookie defaults IdentityServerHost does** — and fixing
that on IdentityServerHost does nothing for this project; they're separate ASP.NET Core
apps with separate `Program.cs` files. Missing this fix here specifically was a real,
confirmed bug (found by inspecting raw `Set-Cookie` headers, not by a failing script —
see IdentityServerHost's README's Phase 4 gotcha #1 for the full story): this app's own
`idsrv` session cookie never survived a real browser's redirect back into this app's own
`/connect/authorize/callback`, so Carol's login here never stuck.

## Running it

This project doesn't do anything on its own — it just needs to be up when
IdentityServerHost tries to federate to it. See
[`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it) for the
full multi-terminal setup.

```bash
cd src/ExternalIdp
dotnet run --urls https://localhost:5011
```

Confirm it's alive on its own:

```bash
curl https://localhost:5011/.well-known/openid-configuration
```

## What's deliberately missing (and why)

- **Any awareness of tenants.** That's the whole point — see
  `IdentityServerHost/README.md`'s Phase 4 section for where tenant-awareness actually
  lives (entirely on the relying-party side).
- **More than one test user.** Carol is enough to prove the federation round trip works;
  a second external user wouldn't teach anything new here.
- **Persistence.** Same as every other project in this sample before Phase 5 — restart
  this app and its (single, in-memory) client registration and signing key regenerate
  from scratch. Harmless here because there's no state *to* lose beyond `tempkey.jwk`.
