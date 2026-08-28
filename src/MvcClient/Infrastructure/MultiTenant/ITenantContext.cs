namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Identity/ITenantContext.cs — an "ambient" per-request holder.
// Registered scoped (see Program.cs), written once by TenantResolutionMiddleware, read by anything
// downstream (controllers, the token client, views) that needs "which tenant is this request for"
// without threading a tenant parameter through every method signature.
public interface ITenantContext
{
    Tenant? Tenant { get; }
    void SetTenant(Tenant tenant);
}
