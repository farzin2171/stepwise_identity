namespace Mini.Infrastructure.MultiTenant;

// Apply counterpart: Equisoft.Apply.Domain/Identity/ITenantContext.cs — an "ambient" per-request holder.
// Registered scoped (see each consuming project's Program.cs), written once by
// TenantResolutionMiddleware, read by anything downstream (controllers, the token client, views) that
// needs "which tenant is this request for" without threading a tenant parameter through every method
// signature.
//
// Extracted into Mini.Infrastructure in Phase 18 — see Tenant.cs in this folder for why.
public interface ITenantContext
{
    Tenant? Tenant { get; }
    void SetTenant(Tenant tenant);
}
