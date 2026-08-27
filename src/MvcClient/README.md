# MvcClient — Phase 2: The MVC Client

A server-side ASP.NET Core MVC app that logs a user in through
[`../IdentityServerHost`](../IdentityServerHost) using the OpenID Connect
**Authorization Code + PKCE** flow. See that project's README for the full write-up of
Phase 2 (the new `Client` entry, the login page IdentityServer needed, and three
wire-level gotchas worth knowing) — this README covers what's specific to this side of
the flow.

## Why "server-side client" matters

This app runs on a server you control, so it can hold a `ClientSecret` the browser never
sees. That's the whole distinction [`../ReactSpa`](../ReactSpa) is built to contrast
with: a React SPA runs *in* the browser, can't keep a secret, and needs a different
client configuration because of it. See its README for the full comparison.

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

## Calling the API

The secure page has a **Call the API** button. It hits `HomeController.CallApi()`,
which calls [`../SampleApi`](../SampleApi) — a separate process on a separate port —
using the *same* access token this app got from IdentityServerHost during login:

```csharp
[Authorize]
public async Task<IActionResult> CallApi()
{
    var accessToken = await HttpContext.GetTokenAsync("access_token");

    var client = httpClientFactory.CreateClient("SampleApi");
    var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity");
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var response = await client.SendAsync(request);
    // ...
}
```

Three things make this work, none of them SampleApi-specific magic:

1. **`options.Scope.Add("api1")`** in `Program.cs` — asking for this scope during login
   is what puts an access token *good for calling SampleApi* into the token response in
   the first place. Without it, `SaveTokens` still stores *an* access token, but it
   won't carry the `api1` scope SampleApi's policy requires — the call would get a
   `401`.
2. **`options.SaveTokens = true`** — already there from Phase 2, for a different reason
   (showing tokens on the secure page). It's the same setting that makes
   `HttpContext.GetTokenAsync("access_token")` return anything at all here.
3. **`builder.Services.AddHttpClient("SampleApi", ...)`** — a named `HttpClient`
   pointed at `http://localhost:5003`. This app never validates the token itself; it
   just attaches it as a `Bearer` header and lets SampleApi do that work independently.

This is the same pattern the real IdG's clients use to call the real IdG's protected
APIs — a client that already has a user's access token from login reuses it, rather
than asking for a *new* token per downstream call.

[`../ReactSpa`](../ReactSpa) has the same button and calls the same endpoint, but
notably **doesn't** need `AddHttpClient`, `GetTokenAsync`, or any server-side code at
all — it just calls `fetch()` directly from the browser with the token already sitting
in `sessionStorage`. It also needed something this app never did: a CORS policy on
SampleApi itself, because that call crosses origins (`:5173` → `:5003`) in a way this
app's server-to-server call never does. See its README for the comparison.

## Running it

See [`../IdentityServerHost/README.md`](../IdentityServerHost/README.md#running-it) —
all three projects need to be running together for any of them to make sense on its own.

Quick version:

```bash
# terminal 1
cd ../IdentityServerHost && dotnet run

# terminal 2
cd . && dotnet run --urls http://localhost:5002

# terminal 3
cd ../SampleApi && dotnet run --urls http://localhost:5003
```

Then browse to `http://localhost:5002`, click *Go to the secure page*, sign in as
`alice` / `alice` or `bob` / `bob`, and click *Call the API*.

Prefer not to click through a browser? [`test-api.ps1`](../../test-api.ps1) (repo root)
drives the same login + API call over raw HTTP.

## About tenant resolution (Phase 3)

IdentityServerHost now resolves and enforces a tenant per login (`acr_values=tenant:
<name>` — see its README's "Phase 3" section) — but this app doesn't send that
parameter yet, so every login here behaves exactly as before: no tenant hint, no
mismatch possible, `bob`/`bob` and `alice`/`alice` both still work unconditionally.
[`test-phase3.ps1`](../../test-phase3.ps1) (repo root) exercises tenant matching against
`reactspa` instead, since it needs no client secret to script. Wiring
`OpenIdConnectChallengeProperties.AcrValues` into a new action here, so this app can
challenge with a tenant hint too, is that phase's suggested practice exercise.
