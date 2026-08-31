using IdentityServerHost.Configurations.Authentication.OpenId;
using Microsoft.AspNetCore.Authentication;

namespace IdentityServerHost.Configurations.Authentication;

// IdG counterpart: Configurations/Authentication/ExternalProviderAuthenticationExtensions.cs. There, this
// has two halves — AddExternalProvidersFromFile (eager, appsettings-driven, what this sample ports) and
// AddExternalProvidersFromDatabase (eager, DB-driven — used only when Duende's dynamic-provider feature
// is off). Both dispatch on provider type and end up calling AddOpenIdConnect/AddSaml exactly once per
// configured provider; only WHERE the provider list comes from differs. This sample has no database yet
// (that's Phase 5), so only the file-based half exists here.
public static class ExternalProviderAuthenticationExtensions
{
    public static AuthenticationBuilder AddExternalProvidersFromFile(this AuthenticationBuilder builder, IConfiguration configuration)
    {
        var options = new ExternalProvidersOptions();
        configuration.GetSection("ExternalProviders").Bind(options);

        foreach (var provider in options.OpenId)
        {
            builder.AddOpenId(provider);
        }

        return builder;
    }
}
