namespace MvcClient.Infrastructure.MultiTenant;

// Apply counterpart: ITenantRepository -> a SQL "Tenants" table (WHERE IsActive AND Key = @key). This
// sample has no database (same simplification as everywhere else in this repo), so it's a hard-coded
// dictionary instead — deliberately a SEPARATE registry from IdentityServerHost's own Tenants.cs, not a
// shared reference to it. That duplication (both "acme"/"globex" here AND in IdentityServerHost) is the
// point, not an oversight: in the real system, Apply's Tenants table and the IdG's tenant registry are
// two independent stores, kept in sync by an ops process, not by sharing code — a mismatch between them
// (a tenant key that exists in one but not the other) is a real, meaningful failure mode this sample can
// now actually reproduce by editing just one of the two files.
public static class Tenants
{
    public static IReadOnlyDictionary<string, Tenant> All => new Dictionary<string, Tenant>
    {
        ["acme"] = new Tenant { Key = "acme", Name = "Acme Corp" },
        ["globex"] = new Tenant { Key = "globex", Name = "Globex Corporation" }
    };

    public static Tenant? Find(string? key) =>
        key is not null && All.TryGetValue(key, out var tenant) ? tenant : null;
}
