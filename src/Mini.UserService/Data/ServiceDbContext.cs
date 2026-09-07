using Microsoft.EntityFrameworkCore;

namespace Mini.UserService.Data;

// Named after the real thing: both Services.User and Services.TenantManagement call their EF context
// "ServiceDbContext" (see the dit-architecture skill's user-service.md / tenantmanagement-service.md).
//
// The important part isn't the name, it's the CONNECTION STRING: this context points at a database
// called MiniUsers, not the MiniIdG that IdentityServerHost's three contexts share. That separation is
// the whole point of this phase. Until now, anything in this repo that wanted a tenant's GUID could in
// principle have read it out of a table in its own database. Now it genuinely cannot — the rows live in
// a database it has no connection string for, so the only way to get them is to call this service's API.
// "Database per service" stops being a slogan and starts being enforced by the absence of a credential.
public class ServiceDbContext(DbContextOptions<ServiceDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<UserIdentityRole> UserIdentityRoles => Set<UserIdentityRole>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.TenantId);
            entity.HasIndex(t => t.Key).IsUnique();
            entity.Property(t => t.Key).HasMaxLength(64);
            entity.Property(t => t.Name).HasMaxLength(256);

            // The three tenants ExternalServicesStub used to hold in a Dictionary<string, string>, with
            // byte-identical GUIDs — that identity is what lets test-phase7.ps1 pass unmodified against
            // this service instead of the stub, which is the actual proof the replacement is
            // behaviour-preserving rather than merely similar.
            //
            // Seeded through HasData (so the rows arrive in the migration, as part of the schema deploy)
            // rather than through a startup seeder. Phase 6 deliberately took row-seeding OUT of
            // IdentityServerHost's startup and moved it to ConfigIngestionTool; putting a second startup
            // seeder back into the repo would contradict that lesson. A migration carrying baseline
            // reference data is a different thing from an app seeding itself at boot.
            entity.HasData(
                new Tenant
                {
                    TenantId = Guid.Parse("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0"),
                    Key = "acme",
                    Name = "Acme Corporation",
                    Description = "Phase 3's first tenant. Has local users and file-configured external providers.",
                    IsActive = true,
                    CreatedDate = SeedDate
                },
                new Tenant
                {
                    TenantId = Guid.Parse("c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e"),
                    Key = "globex",
                    Name = "Globex Corporation",
                    Description = "Phase 3's second tenant. Local users only, no external providers.",
                    IsActive = true,
                    CreatedDate = SeedDate
                },
                new Tenant
                {
                    TenantId = Guid.Parse("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93"),
                    Key = "initech",
                    Name = "Initech",
                    Description = "Phase 9's tenant. No local user and no file-configured provider — a database-backed provider is its only way in.",
                    IsActive = true,
                    CreatedDate = SeedDate
                });
        });

        modelBuilder.Entity<UserIdentityRole>(entity =>
        {
            entity.HasKey(r => r.UserId);
            entity.Property(r => r.UserId).HasMaxLength(256);
            entity.Property(r => r.Role).HasMaxLength(64);

            // The stub's rolesBySubjectId, same single row: subject "1" is alice. Everyone else falls
            // back to "Member" in the endpoint rather than getting a row here, because that fallback is
            // behaviour, not data — see Endpoints/UserEndpoints.cs.
            entity.HasData(new UserIdentityRole { UserId = "1", Role = "Admin" });
        });
    }

    // HasData requires deterministic values: a DateTime.UtcNow here would make every `dotnet ef
    // migrations add` produce a spurious diff, because the model snapshot would never match.
    private static readonly DateTime SeedDate = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

// Field-for-field the real Services.TenantManagement Tenant table (TenantId, Key, Name, Description,
// IsActive, CreatedDate, UpdatedDate). IsActive is the interesting one: the real read API soft-deletes
// by setting it false, and its own docs flag it as unconfirmed whether reads filter on it. This sample
// filters — see Endpoints/TenantEndpoints.cs for why that choice is visible rather than incidental.
public class Tenant
{
    public Guid TenantId { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdatedDate { get; set; }
}

// UserId is whatever subject id IdentityServerHost passes in — "1"/"2" for the local test users, or
// "external:{scheme}:{externalSubjectId}" for a federated one. The real Services.User calls this
// parameter "externalUserId" but the IdG calls the endpoint for every login, local or federated (see
// IdentityServerHost/Services/SampleProfileService.cs), so the name is aspirational there too.
public class UserIdentityRole
{
    public required string UserId { get; set; }
    public required string Role { get; set; }
}
