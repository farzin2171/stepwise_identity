using System.Security.Claims;
using IdentityServerHost.Data;
using Microsoft.EntityFrameworkCore;

namespace IdentityServerHost;

// Persists provisioned external identities in SQL Server via UserDbContext — Phase 5
// replaced the ConcurrentDictionary this used to be with exactly that kind of real
// persistence, same public shape (still async, so ExternalController and
// SampleProfileService didn't have to change at all). IdG counterpart:
// Data/Stores/UserStore.cs, first-login provisioning against its own UserDbContext.
// Registered with the default (scoped) DbContext lifetime now, not AddSingleton<>() —
// the reason it used to need a process-lifetime singleton (provisioning has to survive
// past the request that did it, for the token-issuance request that follows) is exactly
// what the database now does instead.
public class ExternalUserStore(UserDbContext db)
{
    public async Task ProvisionAsync(string subjectId, IEnumerable<Claim> claims)
    {
        var user = await db.Users.Include(u => u.Claims).FirstOrDefaultAsync(u => u.SubjectId == subjectId);
        if (user is null)
        {
            user = new ExternalUser { SubjectId = subjectId };
            db.Users.Add(user);
        }
        else
        {
            db.UserClaims.RemoveRange(user.Claims);
        }

        user.Claims = claims.Select(c => new ExternalUserClaim { SubjectId = subjectId, Type = c.Type, Value = c.Value }).ToList();
        await db.SaveChangesAsync();
    }

    public async Task<List<Claim>?> FindAsync(string subjectId)
    {
        var user = await db.Users.Include(u => u.Claims).FirstOrDefaultAsync(u => u.SubjectId == subjectId);
        return user?.Claims.Select(c => new Claim(c.Type, c.Value)).ToList();
    }
}
