using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors.Handlers;

// The extension point this phase is about: "for this tenant, where does a user's role come from?"
//
// Real counterpart: one of the handlers Services.User declares over DIT.Connectors, feeding the role
// claim IdentityServerHost enriches every token with (GET /user/identities/role/{userId}). The real
// service's chain is Graph → custom web APIs → claims; this one is web API → claim, because Graph is
// a catalog row here and not a working connector.
//
// CASCADING for Acme and Initech. That is a property of the DATA (rows in
// ConnectorHandlerCascadingTenants), not of this class — nothing here knows or cares whether it is
// being walked as a chain of one or a chain of three, which is the point of putting the decision in
// the choice layer.
public class GetUserRoleHandler(
    IConnectorsHandlersTenantsRepository connectorsHandlersTenants,
    IWebApiConnector webApiConnector,
    IClaimConnector claimConnector,
    ILogger<GetUserRoleHandler> logger)
    : ActionHandlerBase<string, string>(connectorsHandlersTenants, logger)
{
    protected override string HandlerName => HandlerNames.GetUserRole;

    protected override async Task<ServiceExtensibilityResult<string>> ExecuteConnectorAsync(
        ConnectorTypes connectorType,
        ConnectorTenant tenant,
        string userId,
        CancellationToken ct)
    {
        // The switch the real ActionHandlerBase subclasses have: "…dispatch to the matching
        // connector…". One arm per type the service supports, and a default that refuses rather than
        // guesses.
        switch (connectorType)
        {
            case ConnectorTypes.WebApi:
                // The tenant's own system answers with its own shape, so the projection out of it
                // happens here, in the handler that knows what it asked for — not in the connector,
                // which is generic over T.
                var webApiResult = await webApiConnector.GetAsync<AcmeUserResponse>(
                    tenant,
                    HandlerName,
                    new WebApiConnectorParameters(Id: userId, OriginUserIdentifier: userId),
                    ct);

                return webApiResult.Success
                    ? ServiceExtensibilityResult<string>.Succeeded(webApiResult.Value?.Role)
                    : ServiceExtensibilityResult<string>.Failed(webApiResult.Error!, webApiResult.Exception);

            case ConnectorTypes.Claim:
                // No userId parameter, and that asymmetry is the whole nature of a claim connector: it
                // reads what the CALLER sent, so it cannot answer a question about a third party. In
                // this sample that means it answers nothing on the login path — see ClaimConnector.cs
                // for why that is structural rather than a wiring mistake.
                return await claimConnector.GetClaimValueAsync(tenant, HandlerName, ct);

            default:
                return NotImplementedInThisSample(connectorType);
        }
    }

    // Only the fields this handler needs off Acme's response. Deliberately not the whole payload: a
    // connector target is somebody else's API, and binding every field it happens to return is how a
    // third party's harmless addition becomes a deserialization failure here.
    private record AcmeUserResponse(string UserId, string Role);
}
