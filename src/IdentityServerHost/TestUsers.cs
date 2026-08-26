using System.Security.Claims;
using Duende.IdentityServer.Test;

namespace IdentityServerHost;

// Hard-coded local accounts, a stand-in for the real IdG's UserStore (SQL-backed, first-login
// provisioning, external-provider claim cascade). Phase 2 has no external IdP yet, so there is nothing to
// provision from — these are seeded once, at process start, exactly as written below.
public static class TestUsers
{
    public static List<TestUser> Users =>
    [
        new TestUser
        {
            SubjectId = "1",
            Username = "alice",
            Password = "alice",
            Claims =
            [
                new Claim("name", "Alice Anderson"),
                new Claim("email", "alice@example.com")
            ]
        },
        new TestUser
        {
            SubjectId = "2",
            Username = "bob",
            Password = "bob",
            Claims =
            [
                new Claim("name", "Bob Brown"),
                new Claim("email", "bob@example.com")
            ]
        }
    ];
}
