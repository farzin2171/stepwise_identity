using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Mini.Infrastructure.ExternalServices;
using Mini.UserService.Connectors.Repositories;

namespace Mini.UserService.Connectors;

// Real counterpart: DIT.Connectors.HTTP's WebApiConnector — "the tenant-aware HTTP client." It is the
// only connector type that makes a network call, and the only one that needs a credential.
//
// The auth mechanism is not invented here. From the dit-architecture reference for Services.User:
// "Custom web APIs — IWebApiConnector: per-tenant/per-handler endpoints and parameterized routes;
// bearer auth via a service-account token; can inject an OriginUserIdentifier header for user
// context." So this reuses the same per-tenant service account Phase 11 introduced for the outbound
// conversion call (userservice-svc.{tenant}) rather than inventing an API-key scheme.
//
// That reuse has a consequence worth naming: one cached token now serves two very different callees.
// TokenClient requests no scope, so the token carries every scope its client is allowed, and the same
// bearer that converts a user id at IdentityServerHost is what reaches Acme's API. A real deployment
// would want a second service account per integration; this sample keeps the real system's single one
// and writes the cost down instead. See the README's Phase 12 section.
public interface IWebApiConnector
{
    Task<ServiceExtensibilityResult<T>> GetAsync<T>(
        ConnectorTenant tenant,
        string handlerName,
        WebApiConnectorParameters parameters,
        CancellationToken ct = default);
}

// Real counterpart: WebApiConnectorParameters, whose real shape carries an Id plus route parameters.
// Two named parameters here instead of a dictionary, because this sample has exactly two route
// templates and a dictionary would hide which keys any given route actually wants.
public record WebApiConnectorParameters(
    string? Id = null,
    string? Email = null,
    string? OriginUserIdentifier = null);

public class WebApiConnector(
    IHttpClientFactory httpClientFactory,
    IWebApiConnectorConfigurationRoutesRepository routes,
    ITokenClient tokenClient,
    IOptions<ExternalServicesConfiguration> externalServices,
    ILogger<WebApiConnector> logger) : IWebApiConnector
{
    public async Task<ServiceExtensibilityResult<T>> GetAsync<T>(
        ConnectorTenant tenant,
        string handlerName,
        WebApiConnectorParameters parameters,
        CancellationToken ct = default)
    {
        var route = await routes.GetRouteAsync(tenant.TenantId, handlerName, ct);
        if (route is null)
        {
            // Chose WebApi, never said where. A configuration fault, so a FAILED result — not
            // Succeeded(null), which would read as "your API says there is no such user."
            return ServiceExtensibilityResult<T>.Failed(
                $"Tenant '{tenant.Key}' selected a WebApi connector for '{handlerName}' but has no host/route configuration.");
        }

        var uri = BuildUri(route, parameters);

        try
        {
            // The registry's ROOT service account, not a named ServiceDefinition's. There is no
            // ServiceDefinitions entry for a connector target, and there cannot be: a connector's host
            // is a row in SQL precisely so that adding a tenant's integration needs no config edit in
            // this process. So the config registry supplies the credential and the database supplies
            // the address — which is a fair summary of the whole connectors design.
            var serviceAccount = externalServices.Value.ServiceAccount
                ?? throw new InvalidOperationException(
                    "ExternalServicesApi:ServiceAccount is not configured — see appsettings.Development.json.");

            // Per-tenant client id and secret: "userservice-svc.acme" for Acme, ".initech" for Initech.
            // This is why ConnectorTenant carries the friendly Key alongside the GUID — the
            // configuration is GUID-keyed and the credential is key-keyed.
            var accessToken = await tokenClient.GetAccessTokenAsync(serviceAccount, tenant.Key, ct);

            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // The real connector's optional user-context header. Without it, everything the tenant's
            // own audit log sees is "userservice-svc.acme did something," which cannot answer "who
            // looked at this record."
            if (parameters.OriginUserIdentifier is not null)
            {
                request.Headers.Add("OriginUserIdentifier", parameters.OriginUserIdentifier);
            }

            var client = httpClientFactory.CreateClient("connectors");
            var response = await client.SendAsync(request, ct);

            // A 404 from the tenant's own API is an ANSWER: this user is not in their system. It must
            // not be a failure, because a cascade has to be able to move on from it, and a
            // non-cascading handler has to be able to report "not found" rather than "broken."
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                logger.LogInformation(
                    "WebApi connector for tenant {TenantKey} / {HandlerName}: {Uri} returned 404 (no such user)",
                    tenant.Key, handlerName, uri);
                return ServiceExtensibilityResult<T>.Succeeded(default);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Everything else — 401, 403, 5xx — is the tenant's integration not working. Initech's
                // mis-pointed host lands exactly here, with a 403 from Acme's own authorization policy.
                var body = await response.Content.ReadAsStringAsync(ct);
                return ServiceExtensibilityResult<T>.Failed(
                    $"WebApi connector for tenant '{tenant.Key}' got {(int)response.StatusCode} from {uri}: {Truncate(body)}");
            }

            return ServiceExtensibilityResult<T>.Succeeded(await response.Content.ReadFromJsonAsync<T>(ct));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The connector MUST NOT throw. A tenant's external system being unreachable is expected
            // weather (the library map's words), and the cascade in ActionHandlerBase decides what to
            // do about it — a thrown exception would unwind past the loop making that decision.
            //
            // OperationCanceledException is deliberately excluded: the caller going away is not the
            // tenant's integration failing, and swallowing it into a "connector error" would report a
            // cancelled request as a broken third party.
            logger.LogWarning(exception,
                "WebApi connector for tenant {TenantKey} / {HandlerName} threw calling {Uri}",
                tenant.Key, handlerName, uri);
            return ServiceExtensibilityResult<T>.Failed(
                $"WebApi connector for tenant '{tenant.Key}' could not reach {uri}: {exception.Message}", exception);
        }
    }

    // Real counterpart: WebApiConnector.BuildUri — "https://acme-api.io/" + "users/{id}" filled in.
    // Deliberately not a general template engine: two known placeholders, escaped, and anything else
    // left alone so a typo in a route row shows up as a 404 with the literal "{whatever}" visible in
    // the URL rather than as a silently dropped segment.
    private static string BuildUri(WebApiConnectorRouteInformation route, WebApiConnectorParameters parameters)
    {
        var filled = route.Route;

        if (parameters.Id is not null)
        {
            // EscapeDataString, because a local subject id is "external:external-idp:ext-1" — colons
            // in a path segment that must survive the trip. This is the same composite-key string
            // CONTEXT.md flags as a Phase 5 shortcut, showing up here as a third-party URL problem.
            filled = filled.Replace("{id}", Uri.EscapeDataString(parameters.Id));
        }

        if (parameters.Email is not null)
        {
            filled = filled.Replace("{email}", Uri.EscapeDataString(parameters.Email));
        }

        return $"{route.Host.TrimEnd('/')}/{filled.TrimStart('/')}";
    }

    private static string Truncate(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
