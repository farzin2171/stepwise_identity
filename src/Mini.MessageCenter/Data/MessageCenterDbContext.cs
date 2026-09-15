using Microsoft.EntityFrameworkCore;

namespace Mini.MessageCenter.Data;

/// <summary>
/// Mini.MessageCenter's own LocalDB database, following the "database per service" convention
/// every other Mini.* service in this repo already follows (see CONTEXT.md's "MiniUsers" entry) —
/// no other process holds a connection string for it.
/// </summary>
public class MessageCenterDbContext : DbContext
{
    public MessageCenterDbContext(DbContextOptions<MessageCenterDbContext> options) : base(options) { }

    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WebhookSubscription>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.CallbackUrl).IsRequired();
            entity.Property(e => e.Secret).IsRequired();
            // Null = unscoped, receives every tenant's events. Not an empty string — see
            // CONTEXT.md's "Webhook subscription" entry.
            entity.Property(e => e.TenantKey).IsRequired(false);
            entity.Property(e => e.Name).IsRequired();
        });

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SubscriptionId).IsRequired();
            entity.Property(e => e.EventSummary).IsRequired();
            entity.Property(e => e.AttemptedAtUtc).IsRequired();
            entity.Property(e => e.Success).IsRequired();

            entity.HasIndex(e => e.SubscriptionId);
        });
    }

    /// <summary>
    /// Two seeded subscriptions, no admin API yet — the same "seed the rows, build the API later"
    /// shape Mini.AuthorizationService's Policies used before Phase 11-era management endpoints
    /// existed for other tables. See CONTEXT.md's "Webhook subscription" entry.
    /// </summary>
    public static void SeedData(MessageCenterDbContext db)
    {
        if (db.WebhookSubscriptions.Any()) return;

        db.WebhookSubscriptions.AddRange(
            new WebhookSubscription
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Mini.AcmeApi (acme-scoped)",
                CallbackUrl = "https://localhost:5014/webhooks/policy-changed",
                // A shared secret hardcoded in a seed row is exactly the kind of thing a real
                // subscription-management API would generate and store securely instead — see
                // "Where this sample simplifies" in this project's README.
                Secret = "acme-webhook-secret-do-not-use-in-prod",
                TenantKey = "acme"
            },
            new WebhookSubscription
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "WebhookReceiverStub (unscoped)",
                CallbackUrl = "https://localhost:5018/webhook",
                Secret = "stub-webhook-secret-do-not-use-in-prod",
                TenantKey = null
            });

        db.SaveChanges();
    }
}

/// <summary>
/// A stored receiver: where to POST, what shared secret to sign with, and which tenant's events it
/// wants (null = every tenant). See CONTEXT.md's "Webhook subscription" entry.
/// </summary>
public class WebhookSubscription
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string CallbackUrl { get; set; }
    public required string Secret { get; set; }
    public string? TenantKey { get; set; }
}

/// <summary>
/// One outbound delivery, successful or not — this is how "did this event ever land" is answerable
/// without a dead-letter queue (Phase 20 deliberately has none, see the README).
/// </summary>
public class DeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public required string EventSummary { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public bool Success { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? Error { get; set; }
}
