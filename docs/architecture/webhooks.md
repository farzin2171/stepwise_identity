# Webhooks: bus → Mini.MessageCenter → subscriber

Phase 20's addition: `Mini.MessageCenter` (`:5017`) consumes `PolicyChangedEvent` off the real
RabbitMQ bus (Phase 19) and delivers a signed HTTP POST to every webhook subscription that matches
the event's tenant.

```
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

**No real publisher exists yet.** Nothing in production code calls `IPublishEndpoint.Publish` for
`PolicyChangedEvent` — that's Phase 21's job (`Mini.AuthorizationService` gains a policy-admin API
and publishes the event when a `Policy` row changes). Until then, `Mini.MessageCenter` exposes a
throwaway diagnostic endpoint, `POST /api/v1/test/publish-policy-changed`, purely so
`test-phase20.ps1` has something to trigger delivery with — the same shape Phase 13 used to prove
`Mini.AuthorizationService` worked before Phase 14 wired a real caller into it. That endpoint is
documented in `Mini.MessageCenter/Program.cs` as removable once Phase 21 ships.

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
arbitrary caller from POSTing a fake payload to Acme's endpoint. Worth fixing if this arc continues
past Phase 23, not fixed here.

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
- **No real publisher until Phase 21.** This phase proves the consumer and delivery path work; it
  does not prove any real system change triggers a webhook yet.
