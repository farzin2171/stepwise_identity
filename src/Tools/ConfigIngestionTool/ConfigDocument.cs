using Duende.IdentityServer.Models;

namespace ConfigIngestionTool;

// The JSON shape this tool reads — IdG counterpart: whatever format the real, since-deleted
// IdentityGatewayConfigurationExporter tool consumed before writing into the same standard Duende
// tables this sample's ConfigurationDbContext now uses. That tool's actual input format is lost along
// with it; this shape is this course's own design, kept as close to Duende's own model types as JSON
// allows.
public class ConfigDocument
{
    public List<IdentityResourceDto> IdentityResources { get; set; } = [];
    public List<ApiScopeDto> ApiScopes { get; set; } = [];
    public List<ApiResourceDto> ApiResources { get; set; } = [];
    public List<ClientDto> Clients { get; set; } = [];
}

// "kind" covers the two standard scopes every OIDC server needs (IdentityResources.OpenId()/Profile()
// are C# factory helpers, not just objects with those names) — anything else is a custom resource
// declared inline, the same as Config.cs's own "tenant" entry used to be.
public class IdentityResourceDto
{
    public string? Kind { get; set; }
    public string? Name { get; set; }
    public string? DisplayName { get; set; }
    public List<string> UserClaims { get; set; } = [];

    public string Key => Kind ?? Name ?? throw new InvalidOperationException("An identity resource needs either \"kind\" or \"name\".");

    public IdentityResource ToModel() => Kind switch
    {
        "OpenId" => new IdentityResources.OpenId(),
        "Profile" => new IdentityResources.Profile(),
        null => new IdentityResource
        {
            Name = Name ?? throw new InvalidOperationException("A custom identity resource needs a \"name\"."),
            DisplayName = DisplayName,
            UserClaims = UserClaims
        },
        _ => throw new InvalidOperationException($"Unknown identity resource kind \"{Kind}\" — expected \"OpenId\", \"Profile\", or omitted for a custom resource.")
    };
}

public class ApiScopeDto
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }

    public ApiScope ToModel() => new(Name, DisplayName ?? Name);
}

public class ApiResourceDto
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public List<string> Scopes { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];

    public ApiResource ToModel() => new(Name, DisplayName ?? Name)
    {
        Scopes = Scopes,
        UserClaims = UserClaims
    };
}

public class ClientDto
{
    public required string ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public bool RequireClientSecret { get; set; } = true;
    public List<string> AllowedGrantTypes { get; set; } = [];
    public bool RequirePkce { get; set; }
    public bool RequireConsent { get; set; }
    public List<string> RedirectUris { get; set; } = [];
    public List<string> PostLogoutRedirectUris { get; set; } = [];
    public List<string> AllowedCorsOrigins { get; set; } = [];
    public List<string> AllowedScopes { get; set; } = [];

    public Client ToModel()
    {
        var client = new Client
        {
            ClientId = ClientId,
            RequireClientSecret = RequireClientSecret,
            AllowedGrantTypes = AllowedGrantTypes,
            RequirePkce = RequirePkce,
            RequireConsent = RequireConsent,
            RedirectUris = RedirectUris,
            PostLogoutRedirectUris = PostLogoutRedirectUris,
            AllowedCorsOrigins = AllowedCorsOrigins,
            AllowedScopes = AllowedScopes
        };

        // Hashed here, at ingestion time, exactly like Config.cs's own new Secret("...".Sha256()) used
        // to be — the JSON file (and this tool's console output) never carries the hash, only the
        // plaintext secret a real deployment would pull from a vault instead.
        if (ClientSecret is not null)
        {
            client.ClientSecrets.Add(new Secret(ClientSecret.Sha256()));
        }

        return client;
    }
}
