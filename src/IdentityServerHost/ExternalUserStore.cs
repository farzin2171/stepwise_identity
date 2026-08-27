using System.Collections.Concurrent;
using System.Security.Claims;

namespace IdentityServerHost;

// Persists provisioned external identities for the lifetime of THIS PROCESS — a step up from not
// persisting them at all (IProfileService's context.Subject can't see claims from the first-login
// principal later, see SampleProfileService.cs), but still just a ConcurrentDictionary: restart this
// app and Carol's provisioned identity is gone, forcing a full re-federation to ExternalIdp for no real
// reason. IdG counterpart: Data/Stores/UserStore.cs, first-login provisioning, backed by SQL Server
// instead. Phase 5 replaces this dictionary with exactly that kind of real persistence — same public
// shape (still async, so callers don't change), different backing store.
public class ExternalUserStore
{
    private readonly ConcurrentDictionary<string, List<Claim>> _users = new();

    public Task ProvisionAsync(string subjectId, IEnumerable<Claim> claims)
    {
        _users[subjectId] = claims.ToList();
        return Task.CompletedTask;
    }

    public Task<List<Claim>?> FindAsync(string subjectId) =>
        Task.FromResult(_users.GetValueOrDefault(subjectId));
}
