namespace Mini.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Models/Tenant.cs. The real one is an EF entity (SQL-backed,
// with a Metadata key-value collection for arbitrary per-tenant settings); this is the shape without the
// database, matching this whole repo's running theme. Worth naming explicitly: this is a CONSUMING app's
// OWN, independent notion of "what a tenant is" — it does NOT ask IdentityServerHost for tenant metadata,
// the same way the real Apply owns its own Tenants table rather than querying the IdG for tenant details.
//
// Extracted into Mini.Infrastructure in Phase 18: MvcClient (Phase 2) had this as its own copy until
// AgentPortal (Phase 18) became a second, genuine consumer of the exact same claims-based resolution —
// the trigger this repo's own "shared concerns go in Mini.Infrastructure" rule calls for. This is a pure
// move, not a behavior change: same fields, same shape MvcClient's Tenant.cs always had.
public class Tenant
{
    public required string Key { get; init; }
    public required string Name { get; init; }
}
