using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors.Handlers;

// Real counterpart: Services.User's GET /user/identities/email?email=… , resolved through the same
// connector machinery as the role lookup.
//
// Ported for one specific reason: it is NOT cascading for any tenant, and a non-cascading handler is
// the only place a connector failure is allowed to be fatal. Without it, every failure in this sample
// would be absorbed by a chain and the difference between the two choice tables would be invisible —
// which would make the phase title ("cascading") a word rather than a demonstration.
//
// It is also the handler with nothing behind it. The role lookup can fall back to the
// UserIdentityRoles table when no connector is configured; there is no local table of email addresses,
// so "this tenant has not opted in" is simply the end of the road. Two handlers, two different
// meanings for the same NoConnectorConfigured outcome — see Endpoints/UserEndpoints.cs.
public class GetUserByEmailHandler(
    IConnectorsHandlersTenantsRepository connectorsHandlersTenants,
    IWebApiConnector webApiConnector,
    ILogger<GetUserByEmailHandler> logger)
    : ActionHandlerBase<string, UserIdentityInformation>(connectorsHandlersTenants, logger)
{
    protected override string HandlerName => HandlerNames.GetUserByEmail;

    protected override async Task<ServiceExtensibilityResult<UserIdentityInformation>> ExecuteConnectorAsync(
        ConnectorTypes connectorType,
        ConnectorTenant tenant,
        string email,
        CancellationToken ct)
    {
        // No Claim arm: a claim connector reads the caller's token, and "find the user with this email
        // address" is not a question a token about somebody else can answer. The catalog reflects that
        // too — there is no ConnectorHandler row pairing Claim with GetUserByEmail, so the choice layer
        // could not select it even if a tenant tried.
        return connectorType switch
        {
            ConnectorTypes.WebApi => await webApiConnector.GetAsync<UserIdentityInformation>(
                tenant,
                HandlerName,
                new WebApiConnectorParameters(Email: email),
                ct),
            _ => NotImplementedInThisSample(connectorType)
        };
    }
}

// The projection this service hands back to its own callers — named after the real service's
// "*Information" DTO convention. Acme's response happens to have these field names, which is exactly
// the coupling a real connector integration has and a real one solves with AutoMapper profiles per
// tenant. This sample has one WebApi connector target, so the shapes matching is a coincidence worth
// naming rather than a design.
public record UserIdentityInformation(string UserId, string DisplayName, string Email, string Role);
