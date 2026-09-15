using Microsoft.EntityFrameworkCore;

namespace Mini.AuthorizationService.Data;

public class AuthorizationDbContext : DbContext
{
    public AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : base(options) { }

    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<CachedDecision> CachedDecisions => Set<CachedDecision>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Policy>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantKey).IsRequired();
            entity.Property(e => e.Name).IsRequired();
            entity.Property(e => e.ResourceName).IsRequired();
            entity.Property(e => e.Description);
            entity.Property(e => e.PolicyType).IsRequired().HasDefaultValue("Role");
            entity.Property(e => e.Condition); // JSON serialized condition
            entity.Property(e => e.IsEnabled).HasDefaultValue(true);
            entity.Property(e => e.Order).HasDefaultValue(0);

            entity.HasIndex(new[] { nameof(Policy.TenantKey), nameof(Policy.ResourceName), nameof(Policy.IsEnabled) });
        });

        modelBuilder.Entity<CachedDecision>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TenantKey).IsRequired();
            entity.Property(e => e.SubjectKey).IsRequired();
            entity.Property(e => e.ResourceName).IsRequired();
            entity.Property(e => e.ContextHash).IsRequired();
            entity.Property(e => e.Reason).IsRequired();
            entity.Property(e => e.CreatedAtUtc).IsRequired();
            entity.Property(e => e.ExpiresAtUtc).IsRequired();

            // One row per (tenant, subject, resource, context) — a re-evaluation with the same
            // inputs overwrites the previous cached row instead of accumulating stale ones.
            entity.HasIndex(new[] { nameof(CachedDecision.TenantKey), nameof(CachedDecision.SubjectKey), nameof(CachedDecision.ResourceName), nameof(CachedDecision.ContextHash) }).IsUnique();
        });
    }

    public static void SeedData(AuthorizationDbContext db)
    {
        if (db.Policies.Any()) return;

        var policies = new List<Policy>
        {
            new()
            {
                Id = Guid.NewGuid(),
                TenantKey = "acme",
                Name = "Acme Admins",
                ResourceName = "sample-api",
                Description = "Admins can access SampleApi",
                PolicyType = "Role",
                Condition = """{"requiredRoles": ["Admin"]}""",
                IsEnabled = true,
                Order = 1
            },
            new()
            {
                Id = Guid.NewGuid(),
                TenantKey = "globex",
                Name = "Globex Members",
                ResourceName = "sample-api",
                Description = "Members can access SampleApi",
                PolicyType = "Role",
                Condition = """{"requiredRoles": ["Member"]}""",
                IsEnabled = true,
                Order = 1
            }
        };

        db.Policies.AddRange(policies);
        db.SaveChanges();
    }
}

public class Policy
{
    public Guid Id { get; set; }
    public required string TenantKey { get; set; }
    public required string Name { get; set; }
    public required string ResourceName { get; set; }
    public string? Description { get; set; }
    public required string PolicyType { get; set; }
    public string? Condition { get; set; }
    public bool IsEnabled { get; set; }
    public int Order { get; set; }

    public bool EvaluatePolicy(Mini.Infrastructure.Identity.IIdentityContext identity, AuthorizationEvaluationRequest request)
    {
        if (PolicyType == "Role" && Condition != null)
        {
            try
            {
                var lines = System.Text.Json.JsonDocument.Parse(Condition);
                var root = lines.RootElement;

                if (root.TryGetProperty("requiredRoles", out var rolesElement))
                {
                    var requiredRoles = rolesElement.EnumerateArray()
                        .Select(r => r.GetString())
                        .Where(r => r != null)
                        .ToList();

                    // Any authenticated caller — a user token (Subject set) or a service-account
                    // token (ClientId set, no Subject) — carries a role to check via the request
                    // context. Checking identity.Subject alone (Phase 13's original condition) meant
                    // service accounts could never be granted access by any role policy — caught by
                    // actually running test-phase13.ps1 §5 against the live services while building
                    // Phase 15, not by code review.
                    var userRoles = identity.IsAuthenticated
                        ? new[] { request.Context?.GetValueOrDefault("role") ?? "Member" }
                        : Array.Empty<string>();

                    return requiredRoles.Any(role => userRoles.Contains(role));
                }
            }
            catch { }
        }

        return false;
    }
}

// A persisted authorization decision, cached so a repeat call for the same caller/resource/context
// doesn't re-evaluate policies from scratch — and, unlike an in-process Dictionary, survives an
// app restart. The real Services.Authorization does this with Redis (see the README); this sample
// ports the *shape* (cache-aside, with a TTL) onto the same SQL Server database instead of taking a
// Redis dependency.
public class CachedDecision
{
    public Guid Id { get; set; }
    public required string TenantKey { get; set; }
    public required string SubjectKey { get; set; }
    public required string ResourceName { get; set; }
    public required string ContextHash { get; set; }
    public bool Authorized { get; set; }
    public required string Reason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}
