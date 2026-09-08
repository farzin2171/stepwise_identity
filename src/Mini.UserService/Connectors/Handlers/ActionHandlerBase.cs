using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors.Handlers;

// Real counterpart: DIT.Connectors.Domain's ActionHandlerBase<TIn,TOut>. A service declares one
// subclass per extension point, names itself with HandlerName, and implements the dispatch for the
// connector types it supports. The base class owns the part that is the same everywhere: asking the
// choice layer which connector serves this tenant, and — for a cascading handler — walking the chain.
//
// The real base class is smaller than this one: the library map shows GetConnectorType(tenantId)
// returning a single type and the subclass switching on it. The chain walk below is this sample's
// own, and it has to be, because the reference material is explicit that this is the part it cannot
// tell me: "The cascading-handler priority/fallback order is partly inside the DIT connector library"
// (dit-architecture, Services.User, listed under Gaps). So the RULES encoded here are reasoned from
// the schema, not read off the real source, and they are stated below rather than implied.
public abstract class ActionHandlerBase<TInput, TOutput>(
    IConnectorsHandlersTenantsRepository connectorsHandlersTenants,
    ILogger logger)
{
    // Matched against the Handlers table's Name column. A string, so a new extension point is a row
    // plus a subclass and never a schema change.
    protected abstract string HandlerName { get; }

    protected ILogger Logger { get; } = logger;

    // The subclass's actual work: one connector type, one attempt, one result.
    protected abstract Task<ServiceExtensibilityResult<TOutput>> ExecuteConnectorAsync(
        ConnectorTypes connectorType,
        ConnectorTenant tenant,
        TInput input,
        CancellationToken ct);

    // Resolves the chain and walks it. Returns the connector-shaped outcome; deciding what an HTTP
    // caller should see is the endpoint's job (see Endpoints/UserEndpoints.cs), because "no connector
    // is configured" means something different for a role lookup with a local table behind it than
    // for an email lookup with nothing behind it.
    public async Task<ConnectorChainResult<TOutput>> ExecuteAsync(
        ConnectorTenant tenant,
        TInput input,
        CancellationToken ct = default)
    {
        // Cascading rows win when they exist. Both choice tables can in principle hold rows for the
        // same (tenant, handler) — nothing in the schema forbids it — and this sample resolves that by
        // precedence rather than by throwing, on the grounds that a tenant who has been given an
        // ordered chain has been given the more specific answer. Flagged as a decision, not a fact:
        // the real library's precedence here is one of the gaps named above.
        var chain = await connectorsHandlersTenants.GetCascadingConnectorTypesAsync(tenant.TenantId, HandlerName, ct);
        var isCascading = chain.Count > 0;

        if (!isCascading)
        {
            var single = await connectorsHandlersTenants.GetConnectorTypeAsync(tenant.TenantId, HandlerName, ct);
            if (single is null)
            {
                // Neither table has an enabled row. The repository has already logged a warning; this
                // is a legitimate state ("this tenant does not use this feature"), and the caller
                // decides what to do with it.
                return ConnectorChainResult<TOutput>.NoConnectorConfigured();
            }

            chain = [single.Value];
        }

        // The rule that makes "cascading" mean something, and the one thing in this file worth
        // remembering:
        //
        //   In a CASCADE, a connector failure is not fatal. The next connector might answer, so the
        //   chain continues, and a chain that ends without an answer reports "no value" — not an
        //   error. That is what "try one source, fall back to the next" HAS to mean; a cascade whose
        //   first failure aborted the request would have no fallback behaviour at all.
        //
        //   With a SINGLE connector there is nothing to fall back to, so a failure IS the outcome and
        //   surfaces as an error.
        //
        // The uncomfortable corollary, which this sample demonstrates on purpose with Initech's
        // mis-pointed host: a cascade silently absorbs misconfiguration. Every Initech user comes back
        // as the endpoint's fallback role, no request fails, and the only trace is a log line. The
        // errors are collected below and returned so the endpoint CAN surface them; whether anybody
        // looks is the real-world question the design leaves open.
        var errors = new List<string>();

        foreach (var connectorType in chain)
        {
            var result = await ExecuteConnectorAsync(connectorType, tenant, input, ct);

            if (!result.Success)
            {
                errors.Add(result.Error ?? "unknown connector error");
                Logger.LogWarning(
                    "Connector {ConnectorType} failed for tenant {TenantKey} / {HandlerName}: {Error}",
                    connectorType, tenant.Key, HandlerName, result.Error);
                continue;
            }

            if (result.Value is not null)
            {
                Logger.LogInformation(
                    "Connector {ConnectorType} answered for tenant {TenantKey} / {HandlerName}",
                    connectorType, tenant.Key, HandlerName);
                return ConnectorChainResult<TOutput>.Answered(connectorType, result.Value, errors);
            }

            // Succeeded with no value: the connector answered "no such user." Try the next link.
        }

        // The chain is exhausted with no value. Whether that is an ERROR turns on two things, and the
        // first version of this returned on isCascading alone — which was wrong, and wrong in a way
        // only running it revealed. See the README's Phase 12 "things that broke" #2.
        //
        //   Nothing failed          the connector answered "no such user." That is an ANSWER, for a
        //                           single connector exactly as much as for a chain. A tenant's API
        //                           returning 404 must not become a 502.
        //   Something failed, chain  absorbed, by definition — see the comment above the loop.
        //   Something failed, single nothing to fall back to, so the failure IS the outcome.
        return errors.Count > 0 && !isCascading
            ? ConnectorChainResult<TOutput>.Failed(chain[0], errors)
            : ConnectorChainResult<TOutput>.Exhausted(chain, errors);
    }

    // Shared dispatch for the connector type this sample knows about but cannot implement. Every
    // subclass calls this from its default switch arm, so the answer is written once.
    //
    // A FAILED result, not an exception and not "no value": a tenant configured for Azure Graph is
    // configured for something real, and reporting "no such user" would be a lie that looks like data.
    // In a cascade the failure is absorbed like any other, which is exactly right — it is a source
    // that cannot answer.
    protected static ServiceExtensibilityResult<TOutput> NotImplementedInThisSample(ConnectorTypes connectorType) =>
        ServiceExtensibilityResult<TOutput>.Failed(
            $"Connector type '{connectorType}' is a catalog row in this sample, not a working connector. " +
            "AzureGraph would need Microsoft.Graph and a real Entra tenant; see Connectors/ConnectorTypes.cs.");
}

// The four genuinely distinct outcomes of a connector-driven lookup. A bare TOutput? collapses the
// first three into "null," and they are not the same thing to an HTTP caller:
//
//   Answered              a connector produced a value; ConnectorType says which one
//   NoValue               the chain ran to the end and nobody knew this user
//   NoConnectorConfigured no enabled row in either choice table — this tenant has not opted in
//   Error                 a single (non-cascading) connector could not answer at all
public enum ConnectorChainOutcome
{
    Answered,
    NoValue,
    NoConnectorConfigured,
    Error
}

public class ConnectorChainResult<TOutput>
{
    public ConnectorChainOutcome Outcome { get; private init; }
    public TOutput? Value { get; private init; }

    // Which connector actually answered. Worth returning rather than logging only: it is the single
    // most useful thing to see when a role claim is not what somebody expected, and test-phase12.ps1
    // asserts on it — proving the WebApi connector answered rather than the value merely matching.
    public ConnectorTypes? ConnectorType { get; private init; }

    // Every failure the chain absorbed on the way. Non-empty alongside Answered is the interesting
    // case: something IS broken and the request succeeded anyway.
    public IReadOnlyList<string> Errors { get; private init; } = [];

    public static ConnectorChainResult<TOutput> Answered(ConnectorTypes connectorType, TOutput value, IReadOnlyList<string> errors) =>
        new() { Outcome = ConnectorChainOutcome.Answered, Value = value, ConnectorType = connectorType, Errors = errors };

    public static ConnectorChainResult<TOutput> Exhausted(IReadOnlyList<ConnectorTypes> chain, IReadOnlyList<string> errors) =>
        new() { Outcome = ConnectorChainOutcome.NoValue, Errors = errors, ConnectorType = chain.Count == 1 ? chain[0] : null };

    public static ConnectorChainResult<TOutput> NoConnectorConfigured() =>
        new() { Outcome = ConnectorChainOutcome.NoConnectorConfigured };

    public static ConnectorChainResult<TOutput> Failed(ConnectorTypes connectorType, IReadOnlyList<string> errors) =>
        new() { Outcome = ConnectorChainOutcome.Error, ConnectorType = connectorType, Errors = errors };
}
