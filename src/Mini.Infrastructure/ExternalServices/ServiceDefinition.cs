namespace Mini.Infrastructure.ExternalServices;

// Apply counterpart: Equisoft.Apply.Domain/Configuration/ServiceDefinition.cs, verbatim shape and
// fallback behavior. Apply has six of these in production (Configuration, Authorization, Localization,
// UserExperience, User, AssistantManagement); this sample has exactly one ("SampleApi") — same pattern,
// smaller registry, see docs/multitenancy-and-external-services.md for the full comparison.
public class ServiceDefinition
{
    public string Path { get; set; } = string.Empty;
    public string HealthPath { get; set; } = string.Empty;
    public string? BaseUri { get; set; }
    public ServiceAccount? ServiceAccount { get; set; }

    public string GetFullPath() => $"{BaseUri}{Path}";
}
