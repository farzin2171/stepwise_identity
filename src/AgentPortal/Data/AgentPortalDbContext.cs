using Microsoft.EntityFrameworkCore;

namespace AgentPortal.Data;

// Phase 22: Agent Portal's own database, distinct from every other database in this repo (MiniIdG,
// MiniUsers, MiniAuthorization, MiniAcme, MiniMessageCenter) — same "database per service" convention
// CONTEXT.md's MiniUsers entry already names. Holds exactly one table, on purpose: an audit trail of
// policy-edit attempts this app made on the signed-in agent's behalf. The canonical Policy row this
// audit trail talks about never lives here — it stays in Mini.AuthorizationService's own
// AuthorizationDbContext (see PolicyChangeRequest's CONTEXT.md entry for why that split matters).
public class AgentPortalDbContext(DbContextOptions<AgentPortalDbContext> options) : DbContext(options)
{
    public DbSet<PolicyChangeRequest> PolicyChangeRequests => Set<PolicyChangeRequest>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<PolicyChangeRequest>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AgentSubjectId).IsRequired();
            entity.Property(e => e.TenantKey).IsRequired();
            entity.Property(e => e.ResourceName).IsRequired();
            entity.Property(e => e.NewCondition).IsRequired();
            entity.Property(e => e.Outcome).IsRequired();

            // The audit trail's own reason for existing: "show me every change an agent made for
            // their tenant," newest first — see PolicyController.History().
            entity.HasIndex(new[] { nameof(PolicyChangeRequest.TenantKey), nameof(PolicyChangeRequest.RequestedAtUtc) });
        });
    }
}

// Phase 22: who changed which policy, what it was before, what it became, when, and whether the
// downstream write to Mini.AuthorizationService actually succeeded. Deliberately NOT a copy of the
// Policy row itself — OldCondition/NewCondition are a diff of one field (Condition), not a snapshot
// of the whole row, because that field is the only one this phase's UI lets an agent change.
public class PolicyChangeRequest
{
    public Guid Id { get; set; }

    // The signed-in agent's own "sub" claim — never a service account, since this action always runs
    // under [Authorize] against a cookie-authenticated browser session (see PolicyController).
    public required string AgentSubjectId { get; set; }

    public required string TenantKey { get; set; }
    public required string ResourceName { get; set; }

    // Null when no policy existed yet for (TenantKey, ResourceName) before this attempt — the admin
    // endpoint upserts, so "there was nothing here before" is a real, recordable case, not an error.
    public string? OldCondition { get; set; }

    public required string NewCondition { get; set; }
    public DateTime RequestedAtUtc { get; set; }

    // "Succeeded" or "Failed" — recorded distinctly from a successful change rather than only logging
    // successes, because a failed call to Mini.AuthorizationService (service down, 401 because the
    // session's access token expired, etc.) is exactly the kind of thing an audit trail exists to show.
    public required string Outcome { get; set; }

    // Populated only when Outcome is "Failed" — the status code/body Mini.AuthorizationService (or the
    // HttpClient itself) returned, so the audit row explains *why* without needing app logs.
    public string? FailureDetail { get; set; }
}
