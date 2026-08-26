# IdentityServerHost — Phase 1: Foundation

This is the first step of a mini "Identity Gateway" built from scratch, one phase at a
time, so that each phase is a small, runnable slice of what a real OAuth/OIDC
authorization server (like Equisoft's `Applications.IdentityGateway`) actually is under
all its production scaffolding.

Phase 1's job is the smallest possible thing that is still, honestly, an OAuth
authorization server: no clients, no login UI, no database. Just the engine.

```
1. Foundation  ← you are here
2. Clients (MVC + React apps that actually log in)
3. Multi-tenancy
4. External identity providers
5. Persistence (SQL Server instead of in-memory)
6. Data ingestion / config tooling
```

## Why start with (almost) nothing?

A real `Startup.cs` wires `AddIdentityServer()` in the middle of a much bigger pipeline —
SAML2, data protection, audit logging, metrics, message queues, health checks, resilient
HTTP clients, and more — all built around six phases like the ones above. None of that
scaffolding changes what IdentityServer actually *is*. This phase strips everything else
away so the load-bearing part is visible on its own:

> **IdentityServer is middleware.** One call registers its services. One call adds it to
> the request pipeline.

Everything you'll add in later phases (login pages, multi-tenant resolution, external
IdP federation, real databases) is a *customization* layered on top of those two calls —
not a prerequisite for them.

## What's in this project

### `Config.cs`

```csharp
public static class Config
{
    public static IEnumerable<IdentityResource> IdentityResources =>
    [
        new IdentityResources.OpenId(),
        new IdentityResources.Profile()
    ];

    public static IEnumerable<ApiScope> ApiScopes => [];

    public static IEnumerable<Client> Clients => [];
}
```

This is the *shape* of an IdentityServer configuration without any real data in it yet:

- **`IdentityResources`** — these map to OIDC scopes that describe *who the user is*.
  `OpenId` is the mandatory `openid` scope every OIDC request needs. `Profile` is the
  standard `profile` scope (name, picture, etc.).
- **`ApiScopes`** — these would describe *APIs* this server protects (e.g. `orders.read`).
  There are none yet — nothing to protect until Phase 2+.
- **`Clients`** — these would describe *applications allowed to ask this server for
  tokens* (a web app, a SPA, a service). There are none yet, which is deliberate — see
  "What's deliberately missing" below.

In a real IdG, these three lists aren't a static C# class — they're rows in SQL Server,
seeded from JSON config files through a data-ingestion tool (that's Phase 6 territory).
But the *objects* are exactly the same types:
`Duende.IdentityServer.Models.IdentityResource`, `ApiScope`, and `Client` don't know or
care whether they came from memory or a database. That's the whole point of Duende's
store abstraction — swapping Phase 1's in-memory stores for SQL Server later (Phase 5)
won't change this file's shape at all, only *where it's read from*.

### `Program.cs`

```csharp
builder.Services.AddIdentityServer(options =>
       {
           options.KeyManagement.Enabled = false;
       })
       .AddInMemoryIdentityResources(Config.IdentityResources)
       .AddInMemoryApiScopes(Config.ApiScopes)
       .AddInMemoryClients(Config.Clients)
       .AddDeveloperSigningCredential();

var app = builder.Build();

app.UseIdentityServer();

app.Run();
```

Two calls do all the real work:

- **`AddIdentityServer(...)`** registers IdentityServer's services in the DI container —
  token validators, response generators, and the in-memory stores you just configured
  via the `.AddInMemory...()` chain.
- **`UseIdentityServer()`** adds IdentityServer's middleware to the HTTP request
  pipeline. This is what actually answers requests to
  `/.well-known/openid-configuration`, `/connect/authorize`, `/connect/token`, and every
  other OIDC/OAuth endpoint. You never write these endpoints yourself — Duende generates
  all of them from your configuration.

Two details worth calling out:

- **`options.KeyManagement.Enabled = false`** — by default, Duende manages (auto-rotates)
  signing keys for you. We turn that off here because we're supplying our own throwaway
  key with `AddDeveloperSigningCredential()` instead, and don't want the two to conflict.
- **`AddDeveloperSigningCredential()`** is the one line in this file with *no* production
  equivalent. It writes a throwaway RSA signing key to disk (`tempkey.jwk`, created next
  to the project on first run) and reuses it on subsequent runs. A real IdG calls
  `AddCertificates()` instead, which loads an actual certificate from a key vault. We'll
  swap this out in a later phase, once there's a reason to want token signatures that
  survive more than local dev.

## Running it

1. **Start the host**

   ```bash
   cd src/IdentityServerHost
   dotnet run
   ```

   You should see `Now listening on: http://localhost:5000`, plus a warning that you have
   no Duende license key. **That warning is expected and harmless for local dev** — a
   real IdG throws a hard startup error without a license key in Production (a
   deliberate safety net that's out of scope for this learning project).

2. **Fetch the discovery document**

   ```bash
   curl http://localhost:5000/.well-known/openid-configuration
   ```

   Look at `"scopes_supported"`. You should see `["openid", "profile", "offline_access"]`.
   The first two come from `Config.IdentityResources` above. `offline_access` is built
   into Duende IdentityServer itself — it's how refresh tokens get requested, regardless
   of what you configure. Every endpoint URL, every supported grant type and response
   type in this document was generated for you from the single `AddIdentityServer()` call
   — you didn't write any of it.

3. **Fetch the signing key**

   ```bash
   curl http://localhost:5000/.well-known/openid-configuration/jwks
   ```

   This is the *public* half of the throwaway key `AddDeveloperSigningCredential()`
   generated. Every access token and ID token this server will ever issue gets signed
   with the *private* half; any relying party validates that signature against this
   public key. Stop the host, delete `tempkey.jwk`, and run it again — the `kid` value
   changes, because a fresh key gets generated whenever there isn't one on disk already.

## What's deliberately missing (and why)

- **Any client.** With zero entries in `Config.Clients`, every real OAuth flow
  (`/connect/authorize`, `/connect/token`) will reject every request — there's no
  `client_id` that could ever be valid. Adding the first client is exactly where
  **Phase 2** starts.
- **A login UI.** Duende IdentityServer ships no UI of its own — the login page is
  application code *you* write. There's nothing to log in *to* yet (no client asking for
  a login), so there's no UI yet either.
- **Persistence.** Everything above resets to empty on every restart (except the signing
  key, which persists in `tempkey.jwk`). A later phase replaces the in-memory stores with
  a real database.
- **A license key.** Fine for local dev forever. Duende requires (and a real IdG
  enforces) a paid license key in production — that's a licensing/ops concern, not an
  architecture one, so it's out of scope here.

## Try it yourself before moving on

Add `new IdentityResources.Email()` to `Config.IdentityResources` and re-check the
discovery document — try to predict what changes before you look. Then ask yourself:
*"What's the smallest client I could add to make `/connect/authorize` actually work?"*
That question is exactly where Phase 2 starts.
