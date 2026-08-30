using Duende.IdentityServer.EntityFramework.DbContexts;
using Duende.IdentityServer.EntityFramework.Mappers;
using Microsoft.EntityFrameworkCore;

namespace IdentityServerHost.Data;

// Stands in for the real IdG's data-ingestion tool (deleted from that codebase; the
// concept lives on as this course's own Phase 6). Runs on every startup instead of being
// gated behind a flag like the real system's "persistence:serviceDb:ApplyMigrations" +
// DatabaseMigrationStartupTask — simpler for a local teaching sample, safe because it
// only *inserts* Config.cs's rows into ConfigurationDbContext when the table is empty,
// never overwrites what's already there.
public static class SeedData
{
    public static void EnsureSeedData(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var provider = scope.ServiceProvider;

        provider.GetRequiredService<PersistedGrantDbContext>().Database.Migrate();
        provider.GetRequiredService<UserDbContext>().Database.Migrate();

        var configDb = provider.GetRequiredService<ConfigurationDbContext>();
        configDb.Database.Migrate();

        if (!configDb.Clients.Any())
        {
            foreach (var client in Config.Clients)
            {
                configDb.Clients.Add(client.ToEntity());
            }

            configDb.SaveChanges();
        }

        if (!configDb.IdentityResources.Any())
        {
            foreach (var resource in Config.IdentityResources)
            {
                configDb.IdentityResources.Add(resource.ToEntity());
            }

            configDb.SaveChanges();
        }

        if (!configDb.ApiScopes.Any())
        {
            foreach (var apiScope in Config.ApiScopes)
            {
                configDb.ApiScopes.Add(apiScope.ToEntity());
            }

            configDb.SaveChanges();
        }

        if (!configDb.ApiResources.Any())
        {
            foreach (var resource in Config.ApiResources)
            {
                configDb.ApiResources.Add(resource.ToEntity());
            }

            configDb.SaveChanges();
        }
    }
}
