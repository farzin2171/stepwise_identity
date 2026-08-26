# MvcClient — Phase 2: The MVC Client

A server-side ASP.NET Core MVC app that logs a user in through
[`../IdentityServerHost`](../IdentityServerHost) using the OpenID Connect
**Authorization Code + PKCE** flow. See that project's README for the full write-up of
Phase 2 (the new `Client` entry, the login page IdentityServer needed, and three
wire-level gotchas worth knowing) — this README covers what's specific to this side of
the flow.

## Why "server-side client" matters

This app runs on a server you control, so it can hold a `ClientSecret` the browser never
sees. That's the whole distinction that will matter again in the next lesson: a React
SPA runs *in* the browser, can't keep a secret, and needs a different client
configuration because of it.

## What's in this project

### `Program.cs`

```csharp
builder.Services.AddAuthentication(options =>
       {
           options.DefaultScheme = "cookies";
           options.DefaultChallengeScheme = "oidc";
       })
       .AddCookie("cookies")
       .AddOpenIdConnect("oidc", options =>
       {
           options.Authority = "http://localhost:5000";
           options.ClientId = "mvcclient";
           options.ClientSecret = "secret";
           options.ResponseType = "code";
           options.UsePkce = true;
           // ...
       });
```

Two authentication schemes, doing two different jobs:

- **`cookies`** holds this app's own session, once a login has completed. It's the
  `DefaultScheme` — every request checks it first.
- **`oidc`** is only used to *establish* that session. It's the `DefaultChallengeScheme`
  — the scheme ASP.NET Core redirects to when `[Authorize]` finds no valid session. Once
  the OIDC handshake finishes and the cookie is written, `oidc` doesn't run again until
  the cookie expires and a fresh challenge is needed.

A few options worth calling out specifically (the "why", not the "what" — see
`IdentityServerHost`'s README for the wire-level detail behind each):

- **`options.SaveTokens = true`** keeps the `id_token`/`access_token` in the auth cookie,
  which is how `Views/Home/Secure.cshtml` is able to print every claim in the identity.
- **`options.GetClaimsFromUserInfoEndpoint = true`** is required to see claims like
  `name` at all — the code flow's ID token alone doesn't carry them.
- **`options.CorrelationCookie`/`NonceCookie` `SameSite = Lax`** is required for the
  login to complete over plain HTTP on `localhost` — see gotcha #1 in the
  IdentityServerHost README.

### `Controllers/HomeController.cs`

```csharp
public class HomeController : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    public IActionResult Secure() => View(User.Claims);
}
```

`Index` is public. `Secure` has `[Authorize]` — the standard ASP.NET Core authorization
attribute — which is the *entire* mechanism that triggers a login: no session cookie
means no `ClaimsPrincipal`, which means the `oidc` challenge scheme fires and redirects
the browser to IdentityServerHost's `/connect/authorize`.

## Running it

See [`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it) —
both apps need to be running together for either to make sense on its own.

Quick version:

```bash
# terminal 1
cd ../IdentityServerHost && dotnet run

# terminal 2
cd . && dotnet run --urls http://localhost:5002
```

Then browse to `http://localhost:5002`, click *Go to the secure page*, and sign in as
`alice` / `alice` or `bob` / `bob`.
