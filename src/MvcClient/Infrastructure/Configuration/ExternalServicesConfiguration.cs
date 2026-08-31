namespace MvcClient.Infrastructure.Configuration;

// Apply counterpart: Equisoft.Apply.Domain/Configuration/ExternalServicesConfiguration.cs, verbatim
// shape and fallback logic (a service definition inherits the registry's global BaseUri/ServiceAccount
// when it doesn't set its own). This is the "config-driven service registry" this project's
// "Call the API" features are now built on, instead of the hardcoded
// client.BaseAddress = new Uri("http://localhost:5003") from earlier phases.
public class ExternalServicesConfiguration
{
    public string? BaseUri { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }
    public Dictionary<string, ServiceDefinition> ServiceDefinitions { get; set; } = new();

    public ServiceDefinition GetServiceDefinition(string serviceName)
    {
        var serviceDefinition = ServiceDefinitions[serviceName];
        serviceDefinition.ServiceAccount ??= ServiceAccount;
        if (string.IsNullOrWhiteSpace(serviceDefinition.BaseUri))
        {
            serviceDefinition.BaseUri = BaseUri;
        }

        return serviceDefinition;
    }
}
