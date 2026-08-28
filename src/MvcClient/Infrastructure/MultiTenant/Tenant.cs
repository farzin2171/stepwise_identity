namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Models/Tenant.cs. The real one is an EF entity (SQL-backed,
// with a Metadata key-value collection for arbitrary per-tenant settings); this is the shape without the
// database, matching this whole repo's running theme. Worth naming explicitly: this is MvcClient's OWN,
// independent notion of "what a tenant is" — it does NOT ask IdentityServerHost for tenant metadata, the
// same way the real Apply owns its own Tenants table rather than querying the IdG for tenant details.
public class Tenant
{
    public required string Key { get; init; }
    public required string Name { get; init; }
}
