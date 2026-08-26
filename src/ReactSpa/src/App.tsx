import { useState } from 'react'
import { useAuth } from 'react-oidc-context'

function App() {
  const auth = useAuth()
  const [apiResult, setApiResult] = useState<string | null>(null)

  if (auth.isLoading) {
    return <p>Loading...</p>
  }

  if (auth.error) {
    return <p>Auth error: {auth.error.message}</p>
  }

  if (!auth.isAuthenticated) {
    return (
      <div style={{ maxWidth: 480, margin: '4rem auto', fontFamily: 'sans-serif' }}>
        <h2>Mini IdG — React SPA</h2>
        <p>This page is public. No token has been requested yet.</p>
        <button onClick={() => auth.signinRedirect()}>Log in</button>
      </div>
    )
  }

  // auth.user.profile is the decoded ID token's claims — no server call needed to read them. Nothing
  // here validated the token's SIGNATURE, though: that check happened once, inside AuthProvider, right
  // after the token endpoint responded, using the JWKS from /.well-known/openid-configuration/jwks.
  const claims = auth.user?.profile ?? {}

  // Unlike MvcClient, there's no server-side code here to attach the Authorization header for us — this
  // app IS the client, so it calls fetch() directly, with the access token pulled straight out of
  // auth.user (which is really just sessionStorage under the hood). See the README for why that's the
  // whole point of this lesson: the token that protects this call is sitting in the browser, in the
  // clear, not hidden behind a server the way MvcClient's is.
  async function callApi() {
    setApiResult('Loading...')
    const response = await fetch('http://localhost:5003/api/identity', {
      headers: { Authorization: `Bearer ${auth.user?.access_token}` },
    })
    const body = await response.json()
    setApiResult(`HTTP ${response.status} ${response.statusText}\n\n${JSON.stringify(body, null, 2)}`)
  }

  return (
    <div style={{ maxWidth: 480, margin: '4rem auto', fontFamily: 'sans-serif' }}>
      <h2>You're signed in</h2>
      <p>These claims came from the ID token, decoded entirely in the browser — no server involved:</p>
      <table border={1} cellPadding={6} style={{ borderCollapse: 'collapse', width: '100%' }}>
        <thead>
          <tr>
            <th align="left">Claim type</th>
            <th align="left">Value</th>
          </tr>
        </thead>
        <tbody>
          {Object.entries(claims).map(([type, value]) => (
            <tr key={type}>
              <td>{type}</td>
              <td>{String(value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <p>
        <button onClick={callApi}>Call the API</button>{' '}
        <button onClick={() => auth.removeUser()}>Log out (clear local session)</button>
      </p>
      {apiResult && (
        <div>
          <p>Response from <code>GET http://localhost:5003/api/identity</code>:</p>
          <pre style={{ background: '#f4f4f4', border: '1px solid #ddd', padding: '1rem', overflowX: 'auto' }}>
            {apiResult}
          </pre>
        </div>
      )}
    </div>
  )
}

export default App
