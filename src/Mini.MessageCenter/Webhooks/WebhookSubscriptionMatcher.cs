using Mini.MessageCenter.Data;

namespace Mini.MessageCenter.Webhooks;

/// <summary>
/// The one decision this phase's fan-out makes: given an event's tenant and a subscription's scope,
/// does this subscription receive it? Small, but the exact shape the phase conventions call out for a
/// table-driven xunit test (see ConnectorChainTests for the precedent) rather than only an HTTP script
/// — an unscoped subscription matches every tenant, a scoped one matches only its own, and a mismatch
/// must not leak (that's the whole "acme must not see globex" lesson this phase mirrors from
/// Connector's tenant scoping).
/// </summary>
public static class WebhookSubscriptionMatcher
{
    public static bool Matches(WebhookSubscription subscription, string eventTenantKey) =>
        subscription.TenantKey is null || subscription.TenantKey == eventTenantKey;

    public static IReadOnlyList<WebhookSubscription> MatchingSubscriptions(
        IEnumerable<WebhookSubscription> subscriptions, string eventTenantKey) =>
        subscriptions.Where(s => Matches(s, eventTenantKey)).ToList();
}
