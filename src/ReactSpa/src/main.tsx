import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { AuthProvider } from 'react-oidc-context'
import './index.css'
import App from './App.tsx'

// The mirror image of MvcClient/Program.cs's AddOpenIdConnect() block — same protocol, same client
// concept (Authorization Code + PKCE), configured in the browser instead of on a server because there is
// no server here for this app. See the README for why every field below differs from the MVC client's.
const oidcConfig = {
  authority: 'https://localhost:5001',
  client_id: 'reactspa',
  redirect_uri: 'http://localhost:5173/callback',
  post_logout_redirect_uri: 'http://localhost:5173',
  response_type: 'code',
  // "api1" is what makes the access token this app gets back usable against SampleApi — see
  // App.tsx's callApi() and SampleApi's own README for what it checks.
  scope: 'openid profile api1',
  // Removes the ?code=...&state=... query string from the URL bar after the callback completes — the
  // authorization code is single-use and already spent by then; leaving it visible teaches nothing and
  // looks like a live secret.
  onSigninCallback: () => {
    window.history.replaceState({}, document.title, window.location.pathname)
  },
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider {...oidcConfig}>
      <App />
    </AuthProvider>
  </StrictMode>,
)
