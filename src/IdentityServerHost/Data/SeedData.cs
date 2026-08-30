using Duende.IdentityServer.EntityFramework.DbContexts;
using Microsoft.EntityFrameworkCore;

namespace IdentityServerHost.Data;

// Phase 5 had this apply migrations AND seed Config.cs's Clients/Resources if the tables were empty —
// a stand-in for the real IdG's data-ingestion tool, before this course had one of its own. Phase 6
// added a real one (../../Tools/ConfigIngestionTool), so seeding moved there: this class now only
// migrates schema. Running IdentityServerHost with an empty, freshly-migrated database and no clients
// in it is the expected state until someone runs the ingestion tool — the same two-step deploy a real
// IdG actually has (apply schema, then ingest config), not a bug.
public static class SeedData
{
    public static void EnsureDatabasesMigrated(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        provider.GetRequiredService<ConfigurationDbContext>().Database.Migrate();
        provider.GetRequiredService<PersistedGrantDbContext>().Database.Migrate();
        provider.GetRequiredService<UserDbContext>().Database.Migrate();
    }
}
