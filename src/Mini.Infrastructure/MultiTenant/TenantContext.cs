namespace Mini.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Identity/TenantContext.cs — verbatim same shape, right down
// to "SetTenant is the only writer, Tenant is get-only from the outside."
//
// Extracted into Mini.Infrastructure in Phase 18 — see Tenant.cs in this folder for why.
public class TenantContext : ITenantContext
{
    public Tenant? Tenant { get; private set; }

    public void SetTenant(Tenant tenant) => Tenant = tenant;
}
