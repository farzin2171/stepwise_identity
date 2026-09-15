using MassTransit;
using Microsoft.EntityFrameworkCore;
using Mini.Infrastructure.Messaging;
using Mini.MessageCenter.Data;
using Mini.MessageCenter.Webhooks;

namespace Mini.MessageCenter.Messaging;

/// <summary>
/// The first REAL cross-process consumer of Phase 19's message bus. On every
/// <see cref="PolicyChangedEvent"/>, fans it out to every subscription that matches the event's
/// tenant (see <see cref="WebhookSubscriptionMatcher"/>), delivering each one independently so a
/// slow or failing subscriber doesn't block another's delivery.
/// </summary>
public class PolicyChangedEventConsumer : IConsumer<PolicyChangedEvent>
{
    private readonly MessageCenterDbContext _db;
    private readonly WebhookDeliveryService _delivery;
    private readonly ILogger<PolicyChangedEventConsumer> _logger;

    public PolicyChangedEventConsumer(MessageCenterDbContext db, WebhookDeliveryService delivery, ILogger<PolicyChangedEventConsumer> logger)
    {
        _db = db;
        _delivery = delivery;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<PolicyChangedEvent> context)
    {
        var evt = context.Message;
        _logger.LogInformation(
            "PolicyChangedEvent received: tenant={TenantKey} resource={ResourceName}",
            evt.TenantKey, evt.ResourceName);

        var subscriptions = await _db.WebhookSubscriptions.ToListAsync(context.CancellationToken);
        var matching = WebhookSubscriptionMatcher.MatchingSubscriptions(subscriptions, evt.TenantKey);

        foreach (var subscription in matching)
        {
            await _delivery.DeliverAsync(subscription, evt, context.CancellationToken);
        }
    }
}
