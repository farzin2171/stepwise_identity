# Mini.MessageCenter — webhook fan-out for PolicyChangedEvent

Runs on **`https://localhost:5017`**, added in Phase 20. Consumes `PolicyChangedEvent` off the
real RabbitMQ bus Phase 19 built and delivers a signed webhook to every subscriber whose tenant
scope matches the event.

```
1. Foundation ✓
...
19. Message bus (MassTransit/RabbitMQ port into Mini.Infrastructure) ✓
20. Mini.MessageCenter (webhook fan-out) ← this project
21. Mini.AuthorizationService gains a policy-admin API and publishes PolicyChangedEvent ✓
22. AgentPortal gets its own database (PolicyChangeRequest audit trail) and a policy-edit UI (next)
```

## Why this phase

Through Phase 19, the message bus existed and was proven to round-trip a payload — but nothing
consumed it in production, and nothing published to it either. This phase adds the first real
consumer: `Mini.MessageCenter` subscribes to `PolicyChangedEvent` and, when one arrives, looks up
every `WebhookSubscription` whose tenant scope matches, signs a payload for each, and POSTs it with
a retry.

```csharp
// Program.cs
builder.Services.AddMessageBus(builder.Configuration, x =>
{
    x.AddConsumer<PolicyChangedEventConsumer>();
});
```

```csharp
// Messaging/PolicyChangedEventConsumer.cs
public async Task Consume(ConsumeContext<PolicyChangedEvent> context)
{
    var evt = context.Message;
    var subscriptions = await _db.WebhookSubscriptions.ToListAsync(context.CancellationToken);
    var matching = WebhookSubscriptionMatcher.MatchingSubscriptions(subscriptions, evt.TenantKey);

    foreach (var subscription in matching)
    {
        await _delivery.DeliverAsync(subscription, evt, context.CancellationToken);
    }
}
```

## This isn't a port — read this before assuming otherwise

Its name follows this repo's `Mini.X` convention, the same way `Mini.UserService` and
`Mini.AuthorizationService` do, even though the real service it nods at is named
`Services.MessageCenter` in production. That is where the resemblance ends.

Both `Libraries.Infrastructure` and the real `Services.MessageCenter` were checked directly while
designing this phase: **neither has a subscription model, HMAC signing, or delivery-retry logic.**
The real `Services.MessageCenter` calls `Services.Notifications` via a plain, synchronous,
unsigned, fire-once HTTP POST — nothing like a webhook fan-out. So everything below — the
subscription table, the signing scheme, the retry policy — is this course's own invention, not a
port. Only the general shape ("a service downstream services notify") and the name come from the
real one.

## The consumer's decision: which subscriptions receive this event

```csharp
// Webhooks/WebhookSubscriptionMatcher.cs
public static bool Matches(WebhookSubscription subscription, string eventTenantKey) =>
    subscription.TenantKey is null || subscription.TenantKey == eventTenantKey;
```

`TenantKey == null` means unscoped — every tenant's events reach it. A non-null `TenantKey` means
exactly that tenant, never another's. This is the same lesson `Mini.UserService`'s `Connector`
scoping teaches (see `docs/architecture/connectors.md`), applied here to *outbound* delivery
instead of *inbound* lookup — and it's table-tested the same way, in
`tests/StepwiseIdentity.Tests/WebhookSubscriptionMatcherTests.cs`.

## Seeded subscriptions (no admin API yet)

| Subscription | Callback | Scope | Secret used for HMAC |
| --- | --- | --- | --- |
| `Mini.AcmeApi (acme-scoped)` | `https://localhost:5014/webhooks/policy-changed` | `acme` only | `acme-webhook-secret-do-not-use-in-prod` |
| `WebhookReceiverStub (unscoped)` | `https://localhost:5018/webhook` | every tenant | `stub-webhook-secret-do-not-use-in-prod` |

Seeded the same way `Mini.AuthorizationService`'s `Policies` were seeded before any admin API
existed for them (`Data/MessageCenterDbContext.SeedData`) — adding a subscriber today means a code
change and a migration, not an HTTP call. A subscription-management API is explicitly future work.

## Delivery signing

Every outbound POST carries `X-Webhook-Signature`: a hex-encoded HMAC-SHA256 of the raw JSON
payload, keyed by that subscription's own secret (`Webhooks/WebhookDeliveryService.ComputeSignature`).
`WebhookReceiverStub` verifies it with a constant-time comparison. `Mini.AcmeApi` does **not** —
that project already carries none of this repo's DIT-side conventions on purpose (see its own
README), and this phase didn't add signature verification there rather than imply Acme's system
participates in a platform mechanism it has no reason to know about. Documented gap, not an
oversight: nothing currently stops a forged payload from reaching Acme's endpoint.

## Retry and delivery history

`Webhooks/WebhookDeliveryService` wraps the POST in `Mini.Infrastructure/Http/ResiliencePolicies.Retry()`
— the same two-attempt, 2s/4s-backoff policy `AuthorizationClient` already uses elsewhere in this
repo. No circuit breaker: each call targets a different subscription with its own `HttpClient`
invocation, so a shared breaker's tripped/open state wouldn't mean anything meaningful here. No
dead-letter queue either — after retry is exhausted, the only record a delivery ever failed is a
`DeliveryAttempt` row (`Success = false`, `Error` set). `GET /api/v1/deliveries` returns the last
100, newest first, for a human or a test script to answer "did this land."

## Phase 21 closed the gap: the diagnostic endpoint is gone

**Through Phase 20, nothing in production code published `PolicyChangedEvent`.** This service
exposed a throwaway diagnostic endpoint, `POST /api/v1/test/publish-policy-changed`, purely so
`test-phase20.ps1` had a real trigger — the same shape Phase 13 used to prove
`Mini.AuthorizationService` worked before Phase 14 wired SampleApi into it as a real caller.

Phase 21 removed that endpoint exactly as it said it would, once `Mini.AuthorizationService` shipped
a genuine one: `PUT /api/v1/authorization/policies/{tenantKey}/{resourceName}` on that service now
calls `Publish` on every successful write. `test-phase20.ps1` is kept (this repo never deletes a
superseded artifact) but can no longer run past its `/health` checks — `test-phase21.ps1` exercises
this consumer for real instead, triggered by an actual policy change rather than a diagnostic call.

## Endpoints

```
GET  /health                                  -> 200, anonymous
GET  /api/v1/deliveries                       -> last 100 DeliveryAttempt rows, newest first
```

(`POST /api/v1/test/publish-policy-changed` existed through Phase 20 only — see above.)

## Auth on `/api/v1/deliveries`

Left `AllowAnonymous`, and that's a deliberate call rather than an oversight. The closest
precedent in this repo, `Mini.UserService`'s `/api/v2/identity` diagnostic, requires a bearer
token — but that endpoint echoes the **caller's own** identity claims back to them, so a token is
what tells it whose claims to echo. `/api/v1/deliveries` describes server-side delivery history
with no caller-specific data in it; there's nothing for a bearer token to scope by, and no
subscription-management API exists yet for a token to be scoped against anyway. If Phase 21 or
later adds one, this endpoint should very likely move behind the same service-account gating
`Mini.AuthorizationService`'s own diagnostic endpoints use.

## Where this sample simplifies

| Real-world concern | This sample |
| --- | --- |
| Subscription management | Seeded rows; no admin API |
| Delivery guarantee on repeated failure | `DeliveryAttempt` row logs the failure; no dead-letter queue, no alerting |
| Retry | Polly, 2 attempts, fixed backoff — no jitter, no configurable policy per subscription |
| Trigger | Real as of Phase 21 — `Mini.AuthorizationService`'s policy-admin endpoint, not a diagnostic one |
| Acme's signature verification | None — documented gap, see "Delivery signing" above |

## Things that broke, and why they're worth knowing

1. **`AddMessageBus`'s `configureConsumers` callback had never actually been exercised.** Phase 19
   added the parameter but nothing called it with a real consumer — `RabbitMqMessageBusTests` calls
   it directly against `IServiceCollection`, not through a hosted ASP.NET Core app. Wiring
   `x.AddConsumer<PolicyChangedEventConsumer>()` here is the first time that path runs inside
   `WebApplication.CreateBuilder(args).Build()` — it worked without a code change, but it was worth
   verifying, not assuming, since MassTransit's consumer registration has version-specific gotchas
   with scoped DI (a consumer that captures a DbContext must be resolved per-message, not once at
   startup — `AddConsumer<T>` handles this correctly, but it wasn't obvious that it would without
   checking).
2. **EF `HasIndex` on a nullable `TenantKey` column needed no special handling** — worth noting
   because it wasn't obvious going in. SQL Server's default unique-index semantics treat multiple
   NULLs as distinct, which is exactly the behavior an unscoped (`null`) subscription needs; nothing
   here has a uniqueness constraint on `TenantKey` anyway, so this turned out to be a non-issue, but
   it was checked rather than assumed.
3. **Docker/RabbitMQ was unreachable in the sandbox this phase was built in** (same finding as
   Phase 19 — `docker info` connects to a socket path that doesn't exist here). This means
   `test-phase20.ps1` could not be run live in this environment. What WAS verified: `dotnet build`
   across the whole solution, all `dotnet test` cases (including the new
   `WebhookSubscriptionMatcherTests` table), and the EF Core migration generating cleanly. A human
   with a working Docker Desktop needs to run `.\run-all.ps1` and `.\test-phase20.ps1` to confirm the
   live RabbitMQ path — the same caveat Phase 19's own report carried.

## Try it yourself

Stop `WebhookReceiverStub` after `run-all.ps1` brings everything up, then re-publish a test event via
`POST /api/v1/test/publish-policy-changed`. `GET /api/v1/deliveries` will show a `Success: false` row
for the stub's subscription after Polly's two retries are exhausted — and, separately, a
`Success: true` row for Acme's subscription, since that process is still up. Nothing links the two
outcomes together; each subscription's delivery is independent, which is the point of fanning out to a
list rather than looping with a single try/catch around the whole thing.
