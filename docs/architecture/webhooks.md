# Webhooks: bus → Mini.MessageCenter → subscriber

Phase 20's addition: `Mini.MessageCenter` (`:5017`) consumes `PolicyChangedEvent` off the real
RabbitMQ bus (Phase 19) and delivers a signed HTTP POST to every webhook subscription that matches
the event's tenant.

```
     Mini.AuthorizationService (:5015)
       PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}
       - upserts the Policy row's Condition
       - clears matching CachedDecisions (see service-to-service-auth.md / its own README)
       - publishes PolicyChangedEvent
                          │
                          ▼
                    RabbitMQ (Phase 19)
                          │  PolicyChangedEvent
                          ▼
                 Mini.MessageCenter (:5017)
                   - looks up WebhookSubscriptions
                   - matches tenant scope
                   - HMAC-SHA256 signs the payload
                   - POSTs with Polly retry
                   - records a DeliveryAttempt either way
                    ╱                        ╲
                   ╱                          ╲
      Mini.AcmeApi (:5014)          WebhookReceiverStub (:5018)
      POST /webhooks/policy-changed  POST /webhook
      scoped: TenantKey = "acme"     unscoped: every tenant
      does NOT verify the signature  DOES verify the signature
```

**A real publisher exists as of Phase 21.** Through Phase 20, nothing in production code called
`IPublishEndpoint.Publish` for `PolicyChangedEvent` — `Mini.MessageCenter` exposed a throwaway
diagnostic endpoint, `POST /api/v1/test/publish-policy-changed`, purely so `test-phase20.ps1` had
something to trigger delivery with. Phase 21 removed that endpoint exactly as it said it would, and
replaced it with a genuine trigger: `Mini.AuthorizationService`'s new policy-admin endpoint,
`PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}`, calls `Publish` on every successful
write. `test-phase21.ps1` drives the full path — a real policy update, over the real bus, fanned out
as a real webhook — where `test-phase20.ps1` used to fake the first step.

## This isn't a port

Both `Libraries.Infrastructure` and the real `Services.MessageCenter` were checked directly (see
CONTEXT.md's `Mini.MessageCenter` entry): neither has a subscription model, HMAC signing, or
delivery retry. The real `Services.MessageCenter` calls `Services.Notifications` via a plain,
synchronous, unsigned, fire-once HTTP POST. So everything below the "matches tenant scope" line is
this course's own invention — only the service's *name* and its "a service downstream services
notify" shape nod at the real one.

## Subscription scoping

A `WebhookSubscription` row's `TenantKey` is either a specific tenant key or `null` (unscoped —
every tenant's events). This mirrors the lesson `connectors.md` already teaches for *inbound* user
lookups, applied here to *outbound* delivery: a scoped subscription must never receive another
tenant's event. `WebhookSubscriptionMatcher.Matches` (`Mini.MessageCenter/Webhooks/`) is the whole
decision, table-tested in `tests/StepwiseIdentity.Tests/WebhookSubscriptionMatcherTests.cs`.

Two subscriptions are seeded (no admin API yet, same as `Mini.AuthorizationService`'s `Policies`
before an admin endpoint existed for them):

| Subscription | Callback | Scope | Verifies the signature? |
| --- | --- | --- | --- |
| `Mini.AcmeApi (acme-scoped)` | `https://localhost:5014/webhooks/policy-changed` | `acme` only | No — see below |
| `WebhookReceiverStub (unscoped)` | `https://localhost:5018/webhook` | every tenant | Yes |

## Signing

Every delivery carries an `X-Webhook-Signature` header: hex-encoded HMAC-SHA256 of the raw JSON
payload, keyed by the subscription's own shared secret. `WebhookReceiverStub` verifies it with a
constant-time comparison before logging the payload — proving the round trip means something, not
just that a POST arrived. `Mini.AcmeApi` does **not** verify it: that file already carries none of
this repo's platform conventions on purpose (see its own file banner — it's a stand-in for a
system a *tenant* owns), and adding verification there would suggest Acme's system participates in
a mechanism it has no reason to know about. This is a real, named gap: nothing currently stops an
arbitrary caller from POSTing a fake payload to Acme's endpoint. Phase 23 (the arc's closing phase)
looked at this again and left it exactly as-is, for the same reason it was never fixed: touching
`Mini.AcmeApi`'s webhook endpoint to add signature verification would mean giving a file that
deliberately carries none of this repo's platform conventions one anyway. Worth fixing if a future
phase revisits Acme's stand-in service specifically, not fixed here.

## Retry and failure

`Mini.Infrastructure/Http/ResiliencePolicies.Retry()` — the same two-attempt, 2s/4s-backoff policy
`AuthorizationClient` already uses. No circuit breaker (each delivery targets a different
subscription's `HttpClient` call, so a shared breaker's state wouldn't mean anything useful here)
and no dead-letter queue: after retry is exhausted, the only record that delivery ever failed is a
`DeliveryAttempt` row with `Success = false` and an `Error` message. `GET /api/v1/deliveries` on
Mini.MessageCenter reads the last 100, for a test script or a human to check "did this land."

## Where this sample simplifies

- **Seeded subscriptions, not admin-managed.** Adding a receiver today means a code change and a
  restart — a subscription-management API is explicitly future work, mirroring
  `Mini.AuthorizationService`'s own `Policies` table before Phase 11-era admin endpoints existed for
  comparable tables.
- **No dead-letter queue.** A permanently-failing subscriber just accumulates `Success = false` rows
  forever; nothing pages anyone or stops retrying future events to it.
- **The policy-admin API (Phase 21) has no role gate.** `PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}`
  requires authentication but accepts any authenticated caller — a deliberate design decision (see
  `Mini.AuthorizationService/README.md`'s Phase 21 section), not an oversight, and it means any
  caller who can reach that endpoint can trigger a webhook fan-out for a tenant that isn't their own.
