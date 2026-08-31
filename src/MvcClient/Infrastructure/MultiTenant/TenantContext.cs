namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Identity/TenantContext.cs — verbatim same shape, right down
// to "SetTenant is the only writer, Tenant is get-only from the outside."
public class TenantContext : ITenantContext
{
    public Tenant? Tenant { get; private set; }

    public void SetTenant(Tenant tenant) => Tenant = tenant;
}
