namespace Mini.UserService.Connectors;

// The three integration TYPES the real DIT.Connectors catalog knows — one row per type in the
// Connectors table (see the library map at C:\MyWork\MyLearning\EqusoftInfra\reference\
// dit-connectors-map.html, and EqusoftInfra Series 4 for what's inside AddConnectors()).
//
// All three are ported as catalog rows because the catalog is data, and a type missing from it can't
// be chosen at all. Only two are ported as working connectors: AzureGraph would need Microsoft.Graph
// and a real Entra tenant, which this repo has ruled out since Phase 1 (nuget.org and LocalDB, nothing
// else). Its row exists, disabled at the base level, and the dispatcher answers "not implemented in
// this sample" rather than pretending — see Handlers/ActionHandlerBase.cs.
public enum ConnectorTypes
{
    WebApi = 1,
    AzureGraph = 2,
    Claim = 3
}

// The extension points. Real counterpart: rows in DIT.Connectors' Handlers table, named by whatever
// the consuming service calls them ("GetUserDetails", "SubmitDocument", …) — a plain string in the
// database, so a new extension point is a row plus a handler class, never a schema change.
//
// Constants rather than an enum precisely because the database column is a string: an enum here would
// imply the set is fixed at compile time, and the whole design exists so it isn't.
public static class HandlerNames
{
    // The role claim IdentityServerHost enriches every token with. CASCADING for the tenants that opt
    // in — this is the handler the phase title is about.
    public const string GetUserRole = "GetUserRole";

    // Real counterpart: Services.User's GET /user/identities/email?email=… . Ported specifically
    // because it is NOT cascading, and a non-cascading handler is the only place a connector failure
    // is allowed to be fatal. See Handlers/ActionHandlerBase.cs.
    public const string GetUserByEmail = "GetUserByEmail";
}

// The two things every connector call needs to know about a tenant, travelling together because they
// come from two different places and neither alone is enough:
//
//   TenantId  — the GUID the connector configuration tables key on, exactly as the real ones do
//               (IIdentityContext.Tenant is a GUID in DIT). Lives in ServiceDbContext's Tenants table.
//   Key       — the friendly key ("acme"), which is what Mini.Infrastructure's TokenClient needs to
//               pick the right per-tenant service-account secret ("{ClientId}.{tenantKey}").
//
// The real Services.User doesn't need this pair because DIT.Identity carries a tenant GUID on the
// token and its ServiceAccounts config is keyed the same way. This sample's suffix convention is
// key-based (see CONTEXT.md's "Service account"), so the GUID-keyed configuration and the key-keyed
// credential have to be resolved together, once, at the endpoint — see Endpoints/UserEndpoints.cs.
public record ConnectorTenant(Guid TenantId, string Key);
