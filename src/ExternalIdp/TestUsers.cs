using System.Security.Claims;
using Duende.IdentityServer.Test;

namespace ExternalIdp;

// A user who exists ONLY here, not in IdentityServerHost's own TestUsers.cs — the point of federation
// is that the relying party doesn't maintain its own password for this person.
public static class TestUsers
{
    public static List<TestUser> Users =>
    [
        new TestUser
        {
            SubjectId = "ext-1",
            Username = "carol",
            Password = "carol",
            Claims =
            [
                new Claim("name", "Carol Chen"),
                new Claim("email", "carol@partner.example.com")
            ]
        }
    ];
}
