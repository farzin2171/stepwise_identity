using Microsoft.EntityFrameworkCore;

namespace Mini.AuthorizationService.Data;

public class AuthorizationDbContext : DbContext
{
    public AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options) : base(options) { }

    public DbSet<Policy> Policies => Set<Policy>();

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

                    var userRoles = identity.Subject != null
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
