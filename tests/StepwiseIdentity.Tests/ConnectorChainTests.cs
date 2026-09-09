using Microsoft.Extensions.Logging.Abstractions;
using Mini.UserService.Connectors;
using Mini.UserService.Connectors.Handlers;
using Mini.UserService.Connectors.Repositories;

namespace StepwiseIdentity.Tests;

// Phase 12's first decision table: given a chain of connectors and what each one did, what is the
// outcome? This is the table `ActionHandlerBase.ExecuteAsync` implements, and it earns a test for the
// most direct reason available — the first version of it was wrong, and running the service is what
// found that out (README's Phase 12 "things that broke" #2).
//
// Driving these rows through HTTP would need a tenant, connector rows, a running Mini.AcmeApi and a
// way to make a third party fail on demand. Driving them here needs a fake repository and a scripted
// list of connector results, which is why the phase conventions put a decision table in xunit.
public class ConnectorChainTests
{
    private static readonly ConnectorTenant Tenant = new(Guid.Parse("8f14e45f-ceea-467e-bd42-05d1a4a6b3f0"), "acme");

    // ---- The core table: a CASCADE absorbs failures, a SINGLE connector does not -----------------

    // The one rule that makes "cascading" mean something. Every row is a state this repo can actually
    // reach with its seeded configuration, named in the comment.
    [Theory]
    // Acme's alice: the WebApi connector answers at position 1, the Claim connector is never asked.
    [InlineData(true, "value:Admin,value:FromClaim", ConnectorChainOutcome.Answered, "Admin", 0)]
    // Acme's bob: the WebApi connector answers "no such user" (a 404 from Acme's own API is an ANSWER),
    // the chain moves on, the Claim connector finds nothing on a self-issued JWT. No error anywhere.
    [InlineData(true, "none,none", ConnectorChainOutcome.NoValue, null, 0)]
    // Acme's bob with the claim-probe token: position 1 has nothing, position 2 does. This is the row
    // that separates "the cascade reached position 2" from "it never got there."
    [InlineData(true, "none,value:UnderwriterFromClaim", ConnectorChainOutcome.Answered, "UnderwriterFromClaim", 0)]
    // Initech: its WebApiConnectorConfiguration.Host names Acme's API, which answers 403. The cascade
    // ABSORBS it and the request succeeds with no value. Note Errors is 1 and the outcome is still not
    // Error — that combination is the uncomfortable finding this phase documents, pinned here so a
    // later change cannot quietly turn a hidden misconfiguration into a visible one (or vice versa).
    [InlineData(true, "error,none", ConnectorChainOutcome.NoValue, null, 1)]
    // Every link in a chain failing is STILL not an error. There was fallback behaviour available and
    // it was used; the request has an answer ("nobody knows this user"), just not a happy one.
    [InlineData(true, "error,error", ConnectorChainOutcome.NoValue, null, 2)]
    // A failure at position 1 does not stop position 2 from answering. This is the whole point.
    [InlineData(true, "error,value:FromClaim", ConnectorChainOutcome.Answered, "FromClaim", 1)]
    //
    // ---- The same inputs, NON-cascading -------------------------------------------------------
    //
    // Acme's email lookup, hit: one connector, it answers.
    [InlineData(false, "value:alice", ConnectorChainOutcome.Answered, "alice", 0)]
    // Acme's email lookup for nobody@acme.test: the connector said "no such user." NOT an error —
    // this is the exact row the first implementation got wrong, returning 502 where a 404 belongs,
    // because it decided on `isCascading` alone and ignored whether anything had actually failed.
    [InlineData(false, "none", ConnectorChainOutcome.NoValue, null, 0)]
    // Initech's email lookup: 403 from Acme's API, and nothing behind it. NOW it is an error, and the
    // caller sees the 502 that the role route's cascade hides for the very same misconfiguration.
    [InlineData(false, "error", ConnectorChainOutcome.Error, null, 1)]
    public async Task ChainOutcomeDependsOnCascadingAndOnWhetherAnythingFailed(
        bool cascading,
        string script,
        ConnectorChainOutcome expectedOutcome,
        string? expectedValue,
        int expectedErrorCount)
    {
        var steps = script.Split(',');
        var handler = new ScriptedHandler(new FakeChainRepository(cascading, steps.Length), steps);

        var result = await handler.ExecuteAsync(Tenant, "input");

        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(expectedValue, result.Value);
        Assert.Equal(expectedErrorCount, result.Errors.Count);
    }

    // Globex: an enabled tenant choice row pointing at a base-DISABLED catalog pairing resolves to
    // nothing, in both tables. Distinct from NoValue on purpose — the role endpoint turns this one
    // into a read of the local UserIdentityRoles table (the Phase 11 behaviour) and NoValue into the
    // "Member" fallback, so collapsing them would change which tenants keep their old roles.
    [Fact]
    public async Task NoEnabledRowInEitherChoiceTableIsItsOwnOutcome()
    {
        var handler = new ScriptedHandler(new FakeChainRepository(cascading: false, chainLength: 0), []);

        var result = await handler.ExecuteAsync(Tenant, "input");

        Assert.Equal(ConnectorChainOutcome.NoConnectorConfigured, result.Outcome);
        Assert.Empty(result.Errors);
    }

    // Cascading rows WIN when both choice tables have rows for the same (tenant, handler). Nothing in
    // the schema forbids that overlap, and this sample resolves it by precedence rather than by
    // throwing — on the grounds that an ordered chain is the more specific instruction. Pinned because
    // it is a decision, not a fact: the real library's precedence here is one of the documented gaps
    // (see ActionHandlerBase.cs).
    [Fact]
    public async Task CascadingRowsTakePrecedenceOverTheNonCascadingChoice()
    {
        var repository = new FakeChainRepository(cascading: true, chainLength: 2);
        var handler = new ScriptedHandler(repository, ["none", "value:FromSecondLink"]);

        var result = await handler.ExecuteAsync(Tenant, "input");

        // Answered from position 2 proves the cascading chain was walked. Had the non-cascading choice
        // won, there would have been exactly one attempt and the outcome would have been NoValue.
        Assert.Equal(ConnectorChainOutcome.Answered, result.Outcome);
        Assert.Equal("FromSecondLink", result.Value);
        Assert.False(repository.NonCascadingWasAsked);
    }

    // AzureGraph is a catalog row with no working connector, and the shared refusal is a FAILED result
    // — not an exception, and not "no such user." In a cascade that means it is absorbed like any other
    // failure, which is correct: it is a source that cannot answer.
    [Fact]
    public async Task AConnectorTypeWithNoImplementationFailsRatherThanReportingNoSuchUser()
    {
        var handler = new ScriptedHandler(
            new FakeChainRepository(cascading: false, chainLength: 1, ConnectorTypes.AzureGraph),
            ["unimplemented"]);

        var result = await handler.ExecuteAsync(Tenant, "input");

        Assert.Equal(ConnectorChainOutcome.Error, result.Outcome);
        Assert.Contains("AzureGraph", result.Errors.Single());
    }

    // A concrete ActionHandlerBase whose connector results come from a script rather than from a
    // network. "value:X" answers X, "none" answers nothing, "error" fails, "unimplemented" takes the
    // base class's shared not-implemented path.
    private class ScriptedHandler(FakeChainRepository repository, string[] script)
        : ActionHandlerBase<string, string>(repository, NullLogger.Instance)
    {
        private int _step;

        protected override string HandlerName => HandlerNames.GetUserRole;

        protected override Task<ServiceExtensibilityResult<string>> ExecuteConnectorAsync(
            ConnectorTypes connectorType,
            ConnectorTenant tenant,
            string input,
            CancellationToken ct)
        {
            var step = script[_step++];

            return Task.FromResult(step switch
            {
                "none" => ServiceExtensibilityResult<string>.Succeeded(null),
                "error" => ServiceExtensibilityResult<string>.Failed($"scripted failure at step {_step}"),
                "unimplemented" => NotImplementedInThisSample(connectorType),
                _ => ServiceExtensibilityResult<string>.Succeeded(step["value:".Length..])
            });
        }
    }

    // Stands in for the choice layer. The real resolution rules (two IsEnabled flags, duplicates,
    // ordering) are the OTHER decision table and are tested against a real DbContext in
    // ConnectorResolutionTests — this one only has to say how long the chain is and which table it
    // came from.
    private class FakeChainRepository(bool cascading, int chainLength, ConnectorTypes type = ConnectorTypes.WebApi)
        : IConnectorsHandlersTenantsRepository
    {
        public bool NonCascadingWasAsked { get; private set; }

        public Task<ConnectorTypes?> GetConnectorTypeAsync(Guid tenantId, string handlerName, CancellationToken ct = default)
        {
            NonCascadingWasAsked = true;
            return Task.FromResult(cascading || chainLength == 0 ? null : (ConnectorTypes?)type);
        }

        public Task<IReadOnlyList<ConnectorTypes>> GetCascadingConnectorTypesAsync(Guid tenantId, string handlerName, CancellationToken ct = default)
        {
            IReadOnlyList<ConnectorTypes> chain = cascading
                ? Enumerable.Repeat(type, chainLength).ToList()
                : [];
            return Task.FromResult(chain);
        }
    }
}
