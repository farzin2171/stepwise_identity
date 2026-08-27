# ReactSpa — Phase 2: The React SPA

A Vite + React + TypeScript app that logs a user in through
[`../IdentityServerHost`](../IdentityServerHost) using the same protocol as
[`../MvcClient`](../MvcClient) — OpenID Connect **Authorization Code + PKCE** — but
configured entirely differently, because of one fact that changes everything else:
**this app has no server.** It's static files a browser downloads and runs. Whatever
this app "knows," anyone with dev tools open knows too, and IdentityServer has to be
configured accordingly.

Built with [react-oidc-context](https://github.com/authts/react-oidc-context), which
wraps [oidc-client-ts](https://github.com/authts/oidc-client-ts) — the library actually
doing PKCE, token parsing, and signature validation under the hood.

## MvcClient vs. ReactSpa — what's different, and why

| | MvcClient | ReactSpa |
|---|---|---|
| Client type | Confidential | Public |
| Client secret | `ClientSecrets = { ... }` | `RequireClientSecret = false` |
| What protects the code exchange | Secret + PKCE (defense in depth) | PKCE alone — the *only* defense |
| Who calls `/connect/token` | The server, over a back-channel HTTP call | The browser itself, via `fetch()` |
| CORS | Not needed — server-to-server | `AllowedCorsOrigins` required |
| Where tokens live after login | Encrypted server-side auth cookie | `sessionStorage`, in the clear |

The last row is worth sitting with. A confidential client's tokens never reach
client-side JavaScript at all — `SaveTokens = true` in MvcClient put them in a cookie
the ASP.NET Core server reads, not the browser. A SPA has no equivalent hiding place:
`sessionStorage` is the least-bad option, and it's still readable by any script that
runs on the page (including a successful XSS payload). This is the architectural reason
the [OAuth for Browser-Based Apps](https://datatracker.ietf.org/doc/html/draft-ietf-oauth-browser-based-apps)
best-practice document — and the BFF (Backend-for-Frontend) pattern — exist: route the
SPA's calls through a confidential backend instead of handling tokens in the browser at
all. This sample does it the plain way on purpose, so the trade-off is visible before
reaching for the BFF pattern as the fix.

## `IdentityServerHost/Config.cs` — the public client

```csharp
new Client
{
    ClientId = "reactspa",
    RequireClientSecret = false,

    AllowedGrantTypes = GrantTypes.Code,
    RequirePkce = true,

    RedirectUris = { "http://localhost:5173/callback" },
    AllowedCorsOrigins = { "http://localhost:5173" },

    AllowedScopes = { IdentityServerConstants.StandardScopes.OpenId, IdentityServerConstants.StandardScopes.Profile }
}
```

`AllowedCorsOrigins` is the one field `mvcclient` never needed. IdentityServer reads it
and wires up CORS for every one of its endpoints automatically — without it, the
browser's preflight `OPTIONS` request to `/connect/token` gets no
`Access-Control-Allow-Origin` header back, and the actual `POST` never leaves the
browser at all. This is *the* gotcha every SPA-onboarding conversation about a real IdG
runs into first — verify it's set before debugging anything else about a browser client
failing to reach the IdG.

## `src/main.tsx` — the entire OIDC client configuration

```tsx
const oidcConfig = {
  authority: 'http://localhost:5000',
  client_id: 'reactspa',
  redirect_uri: 'http://localhost:5173/callback',
  response_type: 'code',
  scope: 'openid profile',
}

createRoot(document.getElementById('root')!).render(
  <AuthProvider {...oidcConfig}>
    <App />
  </AuthProvider>,
)
```

No secret field exists to fill in, because `RequireClientSecret = false` means there's
nothing to authenticate the client itself with. `AuthProvider` generates the PKCE
`code_verifier`/`code_challenge` pair and stashes the verifier in `sessionStorage`
before redirecting to `/connect/authorize`, then retrieves it again when the browser
lands back on `/callback` to complete the token exchange — all of MvcClient's PKCE
bookkeeping (handled for you there by `options.UsePkce = true` in the OIDC *middleware*)
is instead handled here by this *library*, because there's no middleware layer in a
browser to do it.

## `src/App.tsx` — reading the result

```tsx
const auth = useAuth()

if (!auth.isAuthenticated) {
  return <button onClick={() => auth.signinRedirect()}>Log in</button>
}

// auth.user.profile is the decoded ID token's claims — no server call needed to read them
const claims = auth.user?.profile ?? {}
```

`auth.user.profile` is the ID token's payload, base64url-decoded entirely client-side.
Nothing in this component validated the token's *signature* — that check happened once,
inside `AuthProvider`, right after the token endpoint responded, using the JWKS from
`/.well-known/openid-configuration/jwks` (the same document from
`IdentityServerHost`'s Phase 1).

## Calling the API

Like MvcClient, this app has a **Call the API** button that hits
[`../SampleApi`](../SampleApi)'s `/api/identity` and shows the response — but every
piece of *how* it gets there is different, because there's no server here to do the
work MvcClient's `HomeController.CallApi()` does.

```tsx
async function callApi() {
  const response = await fetch('http://localhost:5003/api/identity', {
    headers: { Authorization: `Bearer ${auth.user?.access_token}` },
  })
  const body = await response.json()
  setApiResult(`HTTP ${response.status} ${response.statusText}\n\n${JSON.stringify(body, null, 2)}`)
}
```

Three things make this work, each with a direct counterpart in either `main.tsx` or
`IdentityServerHost/Config.cs` / `SampleApi/Program.cs`:

1. **`scope: 'openid profile api1'`** in `main.tsx` — same reasoning as MvcClient's
   `options.Scope.Add("api1")`: asking for the scope during login is what puts an
   access token good for calling SampleApi into the session in the first place.
   `IdentityServerHost/Config.cs` also had to add `"api1"` to `reactspa`'s
   `AllowedScopes` — asking for a scope a client isn't allowed to request doesn't grant
   it, it just gets silently dropped from the token.
2. **`auth.user?.access_token`** — no `HttpContext.GetTokenAsync()` here; the token is
   just a field on the object `react-oidc-context` already handed back from
   `sessionStorage`. This is the same object whose `.profile` field the claims table
   above reads.
3. **A CORS policy on SampleApi itself.** This is the one piece MvcClient's version of
   this feature never needed. MvcClient calls SampleApi server-to-server — no browser
   involved, so no CORS. This app calls it with the browser's own `fetch()`, from a
   *different origin* (`:5173` calling `:5003`), which makes it subject to the same-origin
   policy every browser enforces. `SampleApi/Program.cs` now has:

   ```csharp
   builder.Services.AddCors(options =>
   {
       options.AddPolicy("ReactSpa", policy => policy
           .WithOrigins("http://localhost:5173")
           .AllowAnyMethod()
           .AllowAnyHeader());
   });
   // ...
   app.UseCors("ReactSpa");
   ```

   Without it, the browser's preflight `OPTIONS` request gets no
   `Access-Control-Allow-Origin` header back and the real `GET` — carrying the
   `Authorization` header — never leaves the browser. This is the *second* time CORS
   shows up in this phase (the first was `AllowedCorsOrigins` on IdentityServerHost, for
   `/connect/token`); every service a public client talks to directly needs its own CORS
   configuration, because CORS is enforced per-origin-pair, not repo-wide.

## Running it

1. **Three terminals**

   ```bash
   # terminal 1
   cd src/IdentityServerHost
   dotnet run

   # terminal 2
   cd src/SampleApi
   dotnet run --urls http://localhost:5003

   # terminal 3
   cd src/ReactSpa
   npm install   # first time only
   npm run dev
   ```

2. **Browse to `http://localhost:5173`**, click **Log in**, sign in as `alice` /
   `alice`. You'll land back on the SPA, still on `localhost:5173`, with a claims table
   rendered from a token that never touched a server — IdentityServer's response went
   straight from `:5000` to this tab.

3. **Click *Call the API***. The response — including `aud: api1` and `scope: api1` —
   renders right below the button, fetched directly from `:5003` by this page's own
   JavaScript.

4. **Open dev tools → Application → Session Storage** and find the
   `oidc.user:http://localhost:5000:reactspa` key. That's the entire session: ID token,
   access token, and expiry, sitting in plaintext, readable by any script on the page —
   including the `fetch()` call above. Compare this to MvcClient's cookie, which you
   can't read from JavaScript at all (`HttpOnly`); this is the concrete version of the
   comparison table above, and the reason a real SPA calling a real API usually goes
   through a BFF instead of doing this directly.

### What's verified, and what isn't

[`test-phase2-spa.ps1`](../../test-phase2-spa.ps1) (repo root) scripts the
authorize → login → token-exchange → CORS-preflight sequence for the login itself;
[`test-spa-api.ps1`](../../test-spa-api.ps1) does the same login (now requesting
`api1` too) and then drives the CORS preflight and authenticated `GET` against
SampleApi, exactly as this page's `fetch()` call does. Both confirm
**IdentityServer's and SampleApi's configuration is correct**. Neither drives real
React/oidc-client-ts code in a browser; that half only gets exercised by the manual
click-through above. This isn't a gap specific to this repo — a corporate proxy blocking
headless-browser binary downloads (Playwright, Puppeteer) is exactly the kind of thing
that makes "click through it yourself once" a genuinely necessary step, not a lesson
skipped out of laziness. Do that click-through before trusting this phase is fully
working end to end.

## What's deliberately missing (and why)

- **Any styling system, routing library, or state management.** Two components' worth
  of logic (`App.tsx`'s auth state and API-call state), one hook (`useAuth`), inline
  styles — enough to prove the protocol, nothing else.
- **A backend of any kind.** That's the whole premise of "public client" — see the BFF
  pattern reference above for what a real production SPA typically does instead of
  handling tokens directly, especially once it starts calling APIs the way this page
  now does.
- **Error handling on the API call beyond the happy path.** A real app would handle a
  401 (expired/invalid token) by redirecting back through `signinRedirect()`, not just
  rendering whatever SampleApi sent back.
- **Sending a tenant hint.** IdentityServerHost can now resolve and enforce a tenant per
  login (`acr_values=tenant:<name>` — see its README's "Phase 3" section), but this app
  never sets `acr_values` on its `signinRedirect()` call, so every login here behaves as
  it always has: `alice` and `bob` both work unconditionally, with no tenant mismatch
  possible. `oidc-client-ts`'s `signinRedirect({ acr_values: 'tenant:acme' })` would be
  the way to add this — see IdentityServerHost's README for the full write-up and
  [`test-phase3.ps1`](../../test-phase3.ps1) for a scripted proof the server-side
  enforcement already works.
- **Ever reaching ExternalIdp's login page for real.** IdentityServerHost can now
  federate Acme's users to [`../ExternalIdp`](../ExternalIdp) — but this app needs zero
  code changes to benefit from that (it only ever talks to IdentityServerHost's own
  `/connect/authorize`). To actually *see* the external option, this app would first
  need to send `acr_values=tenant:acme` (the point above), since Duende has to know a
  tenant to know which external schemes to offer. [`test-phase4.ps1`](../../test-phase4.ps1)
  proves the whole federated round trip works over raw HTTP in the meantime.

## Try it yourself before moving on

Change `RequireClientSecret` back to its default (`true`) on `reactspa` in
`IdentityServerHost/Config.cs`, restart IdentityServerHost, and re-run
`test-phase2-spa.ps1` — read the error the token endpoint gives you.

Separately: comment out `app.UseCors("ReactSpa")` in `SampleApi/Program.cs`, restart
SampleApi, and click *Call the API* again in a real browser (not `test-spa-api.ps1` —
raw `HttpClient` doesn't enforce CORS the way a browser does, so the script would still
pass). Open dev tools' console and read the actual error a browser gives you when CORS
blocks a request — it's different from a 401, and worth being able to recognize on
sight.

Then try adding `acr_values: 'tenant:acme'` to the `signinRedirect()` call in `App.tsx`
and logging in as `bob` — you should see the same "does not belong to Acme Corp"
rejection `test-phase3.ps1` proves over raw HTTP, this time rendered on IdentityServer's
real login page in your own browser.
