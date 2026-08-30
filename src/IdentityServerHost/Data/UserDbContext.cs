using Microsoft.EntityFrameworkCore;

namespace IdentityServerHost.Data;

// The app-owned counterpart to Duende's ConfigurationDbContext/PersistedGrantDbContext
// below (both stock, unmodified) — IdG counterpart: Data/Contexts/UserDbContext.cs,
// same split (Duende owns client/resource/grant persistence, the app owns its own user
// records). Backs ExternalUserStore, replacing its ConcurrentDictionary.
public class UserDbContext(DbContextOptions<UserDbContext> options) : DbContext(options)
{
    public DbSet<ExternalUser> Users => Set<ExternalUser>();
    public DbSet<ExternalUserClaim> UserClaims => Set<ExternalUserClaim>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExternalUser>(entity =>
        {
            entity.HasKey(u => u.SubjectId);
            entity.HasMany(u => u.Claims)
                  .WithOne()
                  .HasForeignKey(c => c.SubjectId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

// SubjectId here is already the local subject id ExternalController builds
// ("external:{scheme}:{externalSubjectId}") — this table doesn't need its own surrogate
// key the way the real IdG's User table does (NEWSEQUENTIALID()), because that composite
// identity is already unique and is exactly what every other lookup keys on.
public class ExternalUser
{
    public required string SubjectId { get; set; }
    public List<ExternalUserClaim> Claims { get; set; } = [];
}

public class ExternalUserClaim
{
    public int Id { get; set; }
    public required string SubjectId { get; set; }
    public required string Type { get; set; }
    public required string Value { get; set; }
}
