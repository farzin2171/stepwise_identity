# WebhookReceiverStub

Runs on **`https://localhost:5018`**, added in Phase 20. A minimal testing aid — in the same spirit
as `ExternalServicesStub` — that receives whatever `Mini.MessageCenter` sends its unscoped
subscription, verifies the HMAC signature, and lets a test script poll what arrived.

This is not shaped like a production service, on purpose: one file, in-memory storage capped at 50
entries, no database, no auth beyond the signature check itself.

## Endpoints

```
POST /webhook            -> verifies X-Webhook-Signature (HMAC-SHA256, constant-time compare),
                             logs the payload, stores it in memory
                             200 { received: true, signatureValid: bool }
GET  /webhook/received   -> last 50 received webhooks, newest first:
                             [{ receivedAtUtc, body, signature, signatureValid }, ...]
GET  /health              -> 200, anonymous
```

## Why it exists rather than reusing `Mini.AcmeApi`

`Mini.AcmeApi`'s subscription is scoped to `acme` only — it can never prove the unscoped case, and
it doesn't verify signatures (see its own README's Phase 20 addition for why). This stub exists to
cover both gaps: it receives every tenant's events, and it's the one thing in this phase that
actually checks the HMAC signature means something, which is what `test-phase20.ps1` relies on to
assert the round trip is genuine rather than just "a POST arrived."

## The secret

`stub-webhook-secret-do-not-use-in-prod`, matching `Mini.MessageCenter`'s seeded subscription row
for this stub exactly (`Data/MessageCenterDbContext.SeedData`). Configured in `appsettings.json`'s
`Webhook:Secret` — a hardcoded shared secret is fine here for the same reason it's fine on the
sender's side: there's no subscription-management API yet on either end.

## Verifying it

```powershell
.\run-all.ps1       # starts this in the default set, before Mini.MessageCenter
.\test-phase20.ps1
```

Started before `Mini.MessageCenter` in `run-all.ps1`'s ordering so its callback URL is already
listening by the time any webhook fires — the same ordering concern `Mini.AcmeApi` has relative to
a login that depends on it.
