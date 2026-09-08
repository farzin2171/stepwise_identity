using Microsoft.EntityFrameworkCore;
using Mini.UserService.Connectors.Data;

namespace Mini.UserService.Connectors.Repositories;

// The SETTINGS-layer repositories: once the choice layer has said "WebApi" or "Claim," these say where
// to go or which claim to read.
//
// Both return projection DTOs (named "*Information", following the real library's convention) rather
// than entities. From the library map's design table: "Callers get exactly the fields they need
// (host + route; claim name); EF entities and their navigation graphs stay behind the repository wall."
// That is load-bearing here in a way it usually isn't — WebApiConnector is registered as a transient
// alongside an HttpClient, and handing it a tracked entity with lazy navigations would tie the
// connector's lifetime to a DbContext it has no reason to know about.

// Real counterpart: DIT.Connectors.Domain's IWebApiConnectorConfigurationRoutesRepository.
public interface IWebApiConnectorConfigurationRoutesRepository
{
    Task<WebApiConnectorRouteInformation?> GetRouteAsync(Guid tenantId, string handlerName, CancellationToken ct = default);
}

// Host comes from the tenant's WebApiConnectorConfiguration, Route from the per-handler row hanging
// off it — one join, one round trip, two tables, because the real schema splits them that way (one
// host per tenant, many routes on it).
public record WebApiConnectorRouteInformation(string Host, string Route);

public class WebApiConnectorConfigurationRoutesRepository(CascadingConnectorDbContext db)
    : IWebApiConnectorConfigurationRoutesRepository
{
    public async Task<WebApiConnectorRouteInformation?> GetRouteAsync(
        Guid tenantId,
        string handlerName,
        CancellationToken ct = default)
    {
        // Null when the tenant has a choice row saying "WebApi" but no settings row saying where — a
        // genuinely reachable state, since choice and settings are separate imports in the real system
        // (and separate tables here). WebApiConnector turns it into a FAILED result rather than a null
        // answer, because "you told me to call your API and never said where" is a configuration fault,
        // not "no such user."
        return await db.WebApiConnectorConfigurationRoutes
            .Where(r => r.WebApiConnectorConfiguration!.TenantId == tenantId
                        && r.Handler!.Name == handlerName)
            .Select(r => new WebApiConnectorRouteInformation(r.WebApiConnectorConfiguration!.Host, r.Route))
            .FirstOrDefaultAsync(ct);
    }
}

// Real counterpart: DIT.Connectors.Domain's claim-connector repository. The architecture reference for
// Services.User notes the real claims connector is only partly documented ("IClaimConnector
// (interface/repository present; handler usage not fully detailed in the repo)"), so the SHAPE here is
// ported from the schema and the handler wiring is this sample's own — flagged rather than presented as
// a faithful port.
public interface IClaimConnectorConfigurationsRepository
{
    Task<string?> GetClaimNameAsync(Guid tenantId, string handlerName, CancellationToken ct = default);
}

public class ClaimConnectorConfigurationsRepository(CascadingConnectorDbContext db)
    : IClaimConnectorConfigurationsRepository
{
    public async Task<string?> GetClaimNameAsync(Guid tenantId, string handlerName, CancellationToken ct = default)
    {
        return await db.ClaimConnectorConfigurations
            .Where(c => c.TenantId == tenantId && c.Handler!.Name == handlerName)
            .Select(c => c.ClaimName)
            .FirstOrDefaultAsync(ct);
    }
}
