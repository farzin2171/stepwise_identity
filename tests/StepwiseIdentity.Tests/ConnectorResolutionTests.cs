using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mini.UserService.Connectors;
using Mini.UserService.Connectors.Data;
using Mini.UserService.Connectors.Repositories;

namespace StepwiseIdentity.Tests;

// Phase 12's second decision table, and the one the whole design rests on: "for this tenant and this
// extension point, which integration handles it?"
//
// These tests run against the REAL seeded configuration — the same HasData rows the migration writes
// into MiniUsers — on the in-memory provider. That is deliberate and is the main reason this file
// exists rather than being folded into ConnectorChainTests: what is being pinned is not just the
// repository's LINQ, it is the shipped decision table. If somebody flips an IsEnabled or moves a row
// between the two choice tables, a login's role claim changes, and this is what says so out loud.
//
// The in-memory provider is a real limitation and worth naming: it is not SQL Server, so it proves
// nothing about SQL translation. Phase 11 got bitten by exactly that (a Guid.ToString() inside a
// query translating to CONVERT(char(36), ...) and returning UPPERCASE — see TenantEndpoints.cs).
// These queries project enums and strings and do no formatting, so there is no translation to get
// wrong; test-phase12.ps1 exercises the same rows through real SQL Server anyway.
public class ConnectorResolutionTests : IDisposable
{
    private static readonly Guid Acme = Guid.Parse("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0");
    private static readonly Guid Globex = Guid.Parse("c9f0f895-fb98-4d75-8d81-7d7c7f4a6b1e");
    private static readonly Guid Initech = Guid.Parse("a3f5b2c1-9d84-4e17-b6a0-2c8e5f1d7b93");
    private static readonly Guid Unknown = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private readonly CascadingConnectorDbContext _db;
    private readonly ConnectorsHandlersTenantsRepository _repository;

    public ConnectorResolutionTests()
    {
        // A distinct database name per test instance — xunit constructs one instance per test, and a
        // shared in-memory store would let the mutating tests below leak into the seeded ones.
        _db = new CascadingConnectorDbContext(new DbContextOptionsBuilder<CascadingConnectorDbContext>()
            .UseInMemoryDatabase($"connectors-{Guid.NewGuid()}")
            .Options);

        // EnsureCreated applies HasData, so the rows under test are the shipped ones.
        _db.Database.EnsureCreated();
        _repository = new ConnectorsHandlersTenantsRepository(_db, NullLogger<ConnectorsHandlersTenantsRepository>.Instance);
    }

    // ---- The shipped cascading chains ------------------------------------------------------------

    // Order is the whole content of the cascading table, so it is asserted as a sequence, not a set.
    // Acme: its own web API first, then a claim on the incoming token. Initech: the same shape, and
    // its WebApi host deliberately names Acme's API.
    [Theory]
    [InlineData("acme", ConnectorTypes.WebApi, ConnectorTypes.Claim)]
    [InlineData("initech", ConnectorTypes.WebApi, ConnectorTypes.Claim)]
    public async Task TheSeededRoleChainsAreInOrder(string tenant, params ConnectorTypes[] expected)
    {
        var chain = await _repository.GetCascadingConnectorTypesAsync(TenantId(tenant), HandlerNames.GetUserRole);

        Assert.Equal(expected, chain);
    }

    // Globex has no cascading rows at all — it is the control case for this phase, still served by the
    // UserIdentityRoles table. And no tenant cascades the email lookup, which is what makes it the one
    // handler where a failure can be fatal.
    [Theory]
    [InlineData("globex", HandlerNames.GetUserRole)]
    [InlineData("acme", HandlerNames.GetUserByEmail)]
    [InlineData("globex", HandlerNames.GetUserByEmail)]
    [InlineData("initech", HandlerNames.GetUserByEmail)]
    public async Task NoOtherTenantOrHandlerCascades(string tenant, string handler)
    {
        Assert.Empty(await _repository.GetCascadingConnectorTypesAsync(TenantId(tenant), handler));
    }

    // ---- The non-cascading choice, and the two kill switches -------------------------------------

    // Acme and Initech both resolve GetUserByEmail to a single WebApi connector.
    [Theory]
    [InlineData("acme")]
    [InlineData("initech")]
    public async Task TheSeededEmailChoiceIsASingleWebApiConnector(string tenant)
    {
        Assert.Equal(ConnectorTypes.WebApi, await _repository.GetConnectorTypeAsync(TenantId(tenant), HandlerNames.GetUserByEmail));
    }

    // The finding this phase most wants remembered. Globex HAS an enabled choice row — it picked
    // AzureGraph for GetUserRole — and resolution still returns nothing, because the base
    // ConnectorHandler pairing is disabled. A lookup needs BOTH flags true.
    //
    // Which means a tenant's configuration can be entirely present and correct and still produce no
    // connector, with the cause a row away in a different table. Same shape as Phase 9's
    // DynamicIdentityProviderEnabled: a vanished integration has two indistinguishable causes.
    [Fact]
    public async Task AnEnabledTenantChoicePointingAtADisabledBasePairingResolvesToNothing()
    {
        // The row really is there and really is enabled — so a null answer below cannot be blamed on
        // Globex simply never having opted in.
        var globexChoice = await _db.ConnectorHandlerTenants
            .Include(t => t.ConnectorHandler)
            .SingleAsync(t => t.TenantId == Globex && t.ConnectorHandler!.HandlerId == 1);
        Assert.True(globexChoice.IsEnabled);
        Assert.False(globexChoice.ConnectorHandler!.IsEnabled);

        Assert.Null(await _repository.GetConnectorTypeAsync(Globex, HandlerNames.GetUserRole));
    }

    // The tenant-level kill switch, from the other direction: leave the base pairing enabled and turn
    // the tenant's own row off. Acme's email lookup stops resolving, and nobody else's changes.
    [Fact]
    public async Task TheTenantLevelFlagAloneIsEnoughToWithdrawAConnector()
    {
        var acmeChoice = await _db.ConnectorHandlerTenants
            .SingleAsync(t => t.TenantId == Acme && t.ConnectorHandlerId == 3);
        acmeChoice.IsEnabled = false;
        await _db.SaveChangesAsync();

        Assert.Null(await _repository.GetConnectorTypeAsync(Acme, HandlerNames.GetUserByEmail));
        Assert.Equal(ConnectorTypes.WebApi, await _repository.GetConnectorTypeAsync(Initech, HandlerNames.GetUserByEmail));
    }

    // Missing rows WARN and return null — a legitimate state ("this tenant does not use this
    // feature"), which is what lets the role endpoint fall back to the local table. An unknown tenant
    // GUID resolves the same way, and the endpoint stops that case earlier by refusing to resolve an
    // unknown tenant key at all.
    [Theory]
    [InlineData("globex", HandlerNames.GetUserByEmail)]
    [InlineData("unknown", HandlerNames.GetUserRole)]
    // A handler name that is not in the Handlers table at all. Null, not a throw: the choice layer's
    // job is to answer "which connector," and "none, I have never heard of that extension point" is
    // an answer of the same kind.
    [InlineData("acme", "GetUserFavouriteColour")]
    public async Task MissingRowsReturnNullRatherThanThrowing(string tenant, string handler)
    {
        Assert.Null(await _repository.GetConnectorTypeAsync(TenantId(tenant), handler));
    }

    // Duplicate rows THROW. Two enabled connectors for one (tenant, handler) in the NON-cascading
    // table is corrupt data: there is no defensible way to pick, and picking arbitrarily means the
    // same tenant is served by different integrations on different requests depending on index order.
    // That presents as "sometimes wrong," which is the worst kind of wrong to diagnose.
    //
    // Note the unique index does NOT prevent this. It is on (TenantId, ConnectorHandlerId), and these
    // two rows name two DIFFERENT ConnectorHandlers — both unique, both enabled, both for GetUserRole.
    // The database cannot express the real invariant, so it lives in the repository and is checked on
    // every read.
    [Fact]
    public async Task TwoEnabledNonCascadingConnectorsForOneHandlerThrow()
    {
        // Enable the AzureGraph pairing Globex already points at, then add a WebApi choice alongside
        // it: two enabled routes to GetUserRole for one tenant.
        (await _db.ConnectorHandlers.SingleAsync(ch => ch.ConnectorHandlerId == 4)).IsEnabled = true;
        _db.ConnectorHandlerTenants.Add(new ConnectorHandlerTenant
        {
            ConnectorHandlerTenantId = 99,
            TenantId = Globex,
            ConnectorHandlerId = 1,
            IsEnabled = true
        });
        await _db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _repository.GetConnectorTypeAsync(Globex, HandlerNames.GetUserRole));

        // The message has to name the tenant, the handler and both types, because the only way to fix
        // corrupt configuration is to know which rows are fighting.
        Assert.Contains(HandlerNames.GetUserRole, exception.Message);
        Assert.Contains(nameof(ConnectorTypes.WebApi), exception.Message);
        Assert.Contains(nameof(ConnectorTypes.AzureGraph), exception.Message);
    }

    // The cascading table does NOT throw on duplicates, and that asymmetry is intentional: duplicates
    // are the point there, and Order disambiguates them. Adding the Claim connector to Globex's chain
    // gives it a chain of one without disturbing anything.
    [Fact]
    public async Task TheCascadingTableAcceptsWhatTheNonCascadingTableRefuses()
    {
        _db.ConnectorHandlerCascadingTenants.Add(new ConnectorHandlerCascadingTenant
        {
            ConnectorHandlerCascadingTenantId = 98,
            TenantId = Globex,
            ConnectorHandlerId = 2,
            Order = 1,
            IsEnabled = true
        });
        await _db.SaveChangesAsync();

        Assert.Equal([ConnectorTypes.Claim], await _repository.GetCascadingConnectorTypesAsync(Globex, HandlerNames.GetUserRole));
    }

    // ---- The settings layer ---------------------------------------------------------------------

    // Host is per tenant and routes are per handler, so a tenant's two extension points necessarily
    // share a host. Initech's is Acme's, which is the planted misconfiguration this phase turns on:
    // asserted here so it reads as a deliberate fixture rather than looking like a copy-paste slip in
    // the seed data.
    [Fact]
    public async Task InitechsWebApiHostIsDeliberatelyAcmes()
    {
        var routes = new WebApiConnectorConfigurationRoutesRepository(_db);

        var acmeRole = await routes.GetRouteAsync(Acme, HandlerNames.GetUserRole);
        var initechRole = await routes.GetRouteAsync(Initech, HandlerNames.GetUserRole);

        Assert.Equal("https://localhost:5014", acmeRole!.Host);
        Assert.Equal(acmeRole.Host, initechRole!.Host);
        Assert.Equal("users/{id}", initechRole.Route);

        // Globex has no settings row at all — the third distinct state, next to "configured and on"
        // and "configured and off."
        Assert.Null(await routes.GetRouteAsync(Globex, HandlerNames.GetUserRole));
    }

    private static Guid TenantId(string key) => key switch
    {
        "acme" => Acme,
        "globex" => Globex,
        "initech" => Initech,
        _ => Unknown
    };

    public void Dispose() => _db.Dispose();
}
