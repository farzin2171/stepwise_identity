using Microsoft.EntityFrameworkCore;
using Mini.UserService.Connectors.Data;

namespace Mini.UserService.Connectors.Repositories;

// Real counterpart: DIT.Connectors.Domain's ConnectorsHandlersTenantsRepository — the one file that
// answers "for this tenant and this extension point, which integration handles it?" Everything else in
// the connectors machinery is plumbing around this question.
//
// The library map records one honest wart in the real file (ConnectorsHandlersTenantsRepository.cs:11-12,
// "the namespaces were NOT adjusted... this refactoring will have to happen one day"). Not ported; a
// stale namespace teaches nothing this sample needs.
public interface IConnectorsHandlersTenantsRepository
{
    // The NON-cascading answer: exactly one connector type, or null.
    Task<ConnectorTypes?> GetConnectorTypeAsync(Guid tenantId, string handlerName, CancellationToken ct = default);

    // The CASCADING answer: connector types in Order, or empty. Empty is not an error — most
    // (tenant, handler) pairs have no cascading rows at all.
    Task<IReadOnlyList<ConnectorTypes>> GetCascadingConnectorTypesAsync(Guid tenantId, string handlerName, CancellationToken ct = default);
}

public class ConnectorsHandlersTenantsRepository(
    CascadingConnectorDbContext db,
    ILogger<ConnectorsHandlersTenantsRepository> logger) : IConnectorsHandlersTenantsRepository
{
    public async Task<ConnectorTypes?> GetConnectorTypeAsync(Guid tenantId, string handlerName, CancellationToken ct = default)
    {
        // Both IsEnabled flags in one WHERE clause: the tenant's own choice row AND the base catalog
        // pairing. A lookup only succeeds when both are true, which is the rule that makes the
        // AzureGraph row Globex picked resolve to nothing.
        var types = await db.ConnectorHandlerTenants
            .Where(t => t.TenantId == tenantId
                        && t.IsEnabled
                        && t.ConnectorHandler!.IsEnabled
                        && t.ConnectorHandler.Handler!.Name == handlerName)
            .Select(t => t.ConnectorHandler!.Connector!.Type)
            .ToListAsync(ct);

        // Duplicate rows THROW; missing rows WARN and return null. Straight from the real library's
        // design table, and the asymmetry is the interesting part:
        //
        //   Two configurations for one (tenant, handler) is corrupt data. There is no defensible way to
        //   pick one, and picking arbitrarily means the same tenant gets served by different
        //   integrations on different requests depending on index order — a bug that presents as
        //   "sometimes wrong," which is the worst kind. Fail loudly.
        //
        //   No configuration is a legitimate state: this tenant does not use this feature. Warning and
        //   returning null is what lets Globex keep being served by the UserIdentityRoles table.
        //
        // Note the unique index on (TenantId, ConnectorHandlerId) does NOT prevent this: two rows can
        // name two DIFFERENT connectors for the same handler and both be unique. The database cannot
        // express "one connector per (tenant, handler)" without a computed column, so the invariant
        // lives here, in code, and is checked on every read.
        if (types.Count > 1)
        {
            throw new InvalidOperationException(
                $"Tenant '{tenantId}' has {types.Count} enabled connectors for handler '{handlerName}' " +
                $"({string.Join(", ", types)}). A non-cascading handler must have exactly one.");
        }

        if (types.Count == 0)
        {
            logger.LogWarning(
                "No enabled connector configured for tenant {TenantId} and handler {HandlerName}",
                tenantId, handlerName);
            return null;
        }

        return types[0];
    }

    public async Task<IReadOnlyList<ConnectorTypes>> GetCascadingConnectorTypesAsync(
        Guid tenantId,
        string handlerName,
        CancellationToken ct = default)
    {
        // Same join, same two flags, plus Order. No duplicate check: duplicates are the POINT here, and
        // Order is what disambiguates them. What the unique index on (TenantId, ConnectorHandlerId)
        // does prevent is the same connector appearing twice in one chain.
        //
        // Two rows sharing an Order value is possible and is left unguarded, deliberately — see the
        // README's Phase 12 section. SQL Server's ordering between them is unspecified, so a chain with
        // a duplicated Order is non-deterministic in exactly the way the throw above exists to prevent
        // for the non-cascading table. It is the one invariant this sample knows about and does not
        // enforce, kept as an open question rather than quietly fixed.
        return await db.ConnectorHandlerCascadingTenants
            .Where(t => t.TenantId == tenantId
                        && t.IsEnabled
                        && t.ConnectorHandler!.IsEnabled
                        && t.ConnectorHandler.Handler!.Name == handlerName)
            .OrderBy(t => t.Order)
            .Select(t => t.ConnectorHandler!.Connector!.Type)
            .ToListAsync(ct);
    }
}
