using Mini.MessageCenter.Data;
using Mini.MessageCenter.Webhooks;

namespace StepwiseIdentity.Tests;

/// <summary>
/// The decision table Phase 20 adds: given a subscription's scope and an event's tenant, does the
/// subscription receive it? Small, but exactly the shape the phase conventions call out for a
/// table-driven test (see ConnectorChainTests) — this mirrors the "acme must not see globex" lesson
/// Connector already teaches, just one boolean instead of a cascade.
/// </summary>
public class WebhookSubscriptionMatcherTests
{
    private static WebhookSubscription Subscription(string? tenantKey) => new()
    {
        Id = Guid.NewGuid(),
        Name = "test",
        CallbackUrl = "https://example.test/webhook",
        Secret = "secret",
        TenantKey = tenantKey
    };

    [Theory]
    // An unscoped subscription (null TenantKey) receives every tenant's events.
    [InlineData(null, "acme", true)]
    [InlineData(null, "globex", true)]
    [InlineData(null, "initech", true)]
    // A scoped subscription receives only its own tenant's events...
    [InlineData("acme", "acme", true)]
    // ...and must NOT receive another tenant's — this is the row that matters: without it, Acme's
    // subscription would see Globex's policy changes, exactly the leak Connector's tenant scoping
    // exists to prevent.
    [InlineData("acme", "globex", false)]
    [InlineData("acme", "initech", false)]
    public void Matches_DependsOnSubscriptionScopeAndEventTenant(string? subscriptionTenantKey, string eventTenantKey, bool expected)
    {
        var subscription = Subscription(subscriptionTenantKey);

        Assert.Equal(expected, WebhookSubscriptionMatcher.Matches(subscription, eventTenantKey));
    }

    [Fact]
    public void MatchingSubscriptions_ReturnsUnscopedPlusOnlyTheMatchingScopedOne()
    {
        var unscoped = Subscription(null);
        var acmeScoped = Subscription("acme");
        var globexScoped = Subscription("globex");

        var matches = WebhookSubscriptionMatcher.MatchingSubscriptions(
            new[] { unscoped, acmeScoped, globexScoped }, "acme");

        Assert.Equal(2, matches.Count);
        Assert.Contains(unscoped, matches);
        Assert.Contains(acmeScoped, matches);
        Assert.DoesNotContain(globexScoped, matches);
    }
}
