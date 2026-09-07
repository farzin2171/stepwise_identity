using System.Security.Claims;
using Mini.Infrastructure.Identity;

namespace StepwiseIdentity.Tests;

// Phase 11 gave Mini.Infrastructure's IIdentityContext a third kind of caller to cope with, and found
// out the hard way that it can only express two. This table is that finding written down in a form
// that fails if anyone "fixes" it by accident.
//
// The three callers that now exist in this repo:
//   1. a user       — has "sub", tenant from the "tenant_id" claim
//   2. a registered service account — no "sub", tenant parsed from the client_id suffix
//   3. IdentityServerHost issuing itself a token (IIdentityServerTools.IssueClientJwtAsync) — no
//      "sub", no scope, no suffix on the client_id
//
// Cases 2 and 3 are INDISTINGUISHABLE here, and that is not a bug in this file. See
// Mini.UserService/Program.cs's authorization block for why it matters and what closes the gap.
public class IdentityContextTests
{
    [Fact]
    public void AnUnauthenticatedPrincipalIsNeitherKind()
    {
        var context = new IdentityContext();
        context.Populate(new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.False(context.IsAuthenticated);
        Assert.Null(context.Subject);
        Assert.Null(context.TenantKey);
    }

    // A user token: "sub" present, so tenant comes from the tenant_id claim and the client_id suffix
    // is ignored even when there is one.
    [Theory]
    [InlineData("1", "mvcclient", "acme", "acme")]
    [InlineData("external:external-idp:ext-1", "reactspa", "acme", "acme")]
    // A user whose client requested "api1" but not "tenant" gets no tenant_id claim in the access
    // token at all — see IdentityServerConfig.json's api1 apiResource userClaims. Null, not a throw.
    [InlineData("1", "mvcclient", null, null)]
    public void AUserIsIdentifiedBySubAndTenantComesFromTheClaim(
        string subject, string clientId, string? tenantIdClaim, string? expectedTenantKey)
    {
        var claims = new List<Claim> { new("sub", subject), new("client_id", clientId) };
        if (tenantIdClaim is not null)
        {
            claims.Add(new Claim("tenant_id", tenantIdClaim));
        }

        var context = Populate(claims);

        Assert.Equal(IdentityType.User, context.IdentityType);
        Assert.Equal(subject, context.Subject);
        Assert.Equal(expectedTenantKey, context.TenantKey);
    }

    // A service-account token: no "sub", tenant parsed from everything after the first dot.
    [Theory]
    [InlineData("mvcclient-svc.acme", "acme")]
    [InlineData("mvcclient-svc.globex", "globex")]
    [InlineData("userservice-svc.initech", "initech")]
    // Split('.', 2) means only the FIRST dot separates, so a tenant key containing a dot survives.
    [InlineData("mvcclient-svc.acme.eu", "acme.eu")]
    // No suffix, so no tenant. "userservice-mgmt-svc" is the real instance of this in the repo: it
    // manages tenants, which is inherently cross-tenant, and this convention has no way to say "all
    // tenants" as distinct from "none." The real DIT.Identity reads an explicit "service_tenant"
    // claim, which can simply be absent.
    [InlineData("userservice-mgmt-svc", null)]
    // The self-issued host JWT — case 3. It lands here, in the Service branch, with no tenant.
    [InlineData("identityserverhost", null)]
    public void AServiceAccountIsIdentifiedByTheAbsenceOfSubAndTenantComesFromTheClientId(
        string clientId, string? expectedTenantKey)
    {
        var context = Populate([new Claim("client_id", clientId)]);

        Assert.Equal(IdentityType.Service, context.IdentityType);
        Assert.Null(context.Subject);
        Assert.Equal(expectedTenantKey, context.TenantKey);
    }

    // The finding, as an assertion. IdentityServerHost's self-issued read JWT and a registered
    // service account are the same IdentityType, so ServiceAccountOnlyFilter — which decides purely
    // on IdentityType — cannot tell a credential that went through /connect/token from one a host
    // signed for itself. What separates them is the scope claim, which only the registered client has.
    //
    // If a later phase adds an explicit caller-kind claim (the real system's "service_isService"), this
    // test is where that change should surface: it will start failing, and that failure is the point.
    [Fact]
    public void TheSelfIssuedHostJwtIsIndistinguishableFromARegisteredServiceAccount()
    {
        // Exactly the claim set IssueClientJwtAsync produces, verified by pointing UserClient at
        // Mini.UserService's /api/v2/identity diagnostic: iss, nbf, iat, exp, client_id, aud. No sub,
        // and no scope.
        var selfIssued = Populate([new Claim("client_id", "identityserverhost"), new Claim("aud", "userapi")]);

        // A registered client-credentials client's token. Same absence of sub — plus a scope.
        var registered = Populate([
            new Claim("client_id", "userservice-mgmt-svc"),
            new Claim("aud", "userapi"),
            new Claim("scope", "userapi")
        ]);

        Assert.Equal(registered.IdentityType, selfIssued.IdentityType);
        Assert.Equal(IdentityType.Service, selfIssued.IdentityType);
        Assert.Null(selfIssued.Subject);
        Assert.Null(registered.Subject);
    }

    private static IdentityContext Populate(IEnumerable<Claim> claims)
    {
        // "Bearer" as the authentication type is what makes ClaimsIdentity.IsAuthenticated true — an
        // identity with claims but no authentication type reports false, which is the same rule the
        // unauthenticated case above relies on.
        var context = new IdentityContext();
        context.Populate(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));
        return context;
    }
}
