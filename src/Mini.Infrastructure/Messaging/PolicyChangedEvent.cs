namespace Mini.Infrastructure.Messaging;

/// <summary>
/// Published on the message bus whenever a <c>Policy</c> row changes. Lives in
/// Mini.Infrastructure — not in Mini.AuthorizationService, which will publish it starting Phase 21,
/// nor in Mini.MessageCenter, which will consume it starting Phase 20 — because both a future
/// publisher and a future consumer need the identical contract type, the same reason
/// <see cref="Mini.Infrastructure.Identity.IIdentityContext"/> lives here rather than in any one
/// project that happens to use it first.
///
/// A named, domain-specific event, not the real library's generic <c>EntityUpdatedEvent</c>
/// envelope (see <c>Libraries.Infrastructure/DIT.MessageQueue/Data/EntityUpdatedEvent.cs</c>) — chosen
/// so a consumer never needs to know that <c>EntityType == "Policy"</c> means something. See
/// CONTEXT.md's "PolicyChangedEvent" entry.
/// </summary>
public sealed record PolicyChangedEvent
{
    public required string TenantKey { get; init; }

    public required string ResourceName { get; init; }

    public string? OldCondition { get; init; }

    public required string NewCondition { get; init; }

    public required DateTimeOffset ChangedAtUtc { get; init; }
}
