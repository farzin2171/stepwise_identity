namespace IdentityServerHost.Configurations.Authentication;

// IdG counterpart: Configurations/Authentication/BaseAuthenticationOptions.cs. The fields every provider
// type shares, regardless of protocol — bind target for the common part of each entry under the
// "ExternalProviders" config section (appsettings.json). Concrete provider option classes (only
// OpenIdConnectProviderOptions so far — see Configurations/Authentication/OpenId/) add their own
// protocol-specific fields on top of this.
public abstract class BaseAuthenticationOptions : IAuthenticationOptions
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EcosystemTenant { get; set; } = string.Empty;
    public FederatedConfigurationOptions? FederatedConfiguration { get; set; }
    public IDictionary<string, string> ClaimMappings { get; set; } = new Dictionary<string, string>();
}
