using Duende.IdentityServer.Configuration;
using IdentityServerHost.Configurations.Authentication.OpenId;
using IdentityServerHost.IdentityServer.Models;
using IdentityServerHost.Models.Constants;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace IdentityServerHost.Configurations.Extensions;

// IdG counterpart: Configurations/Extensions/AdditionalExtensions.cs — the AddDynamicIdentityProviders()
// method, ported with four of its five provider types removed (see IdentityProviderTypes.cs).
public static class DynamicIdentityProviderExtensions
{
    // Two registrations per provider type, and they are easy to confuse:
    //
    //   AddProviderType<THandler, TOptions, TIdentityProvider>(type)
    //       "when you read a row whose Type column is `type`, it becomes a TIdentityProvider, and the
    //        scheme it produces is served by THandler configured through TOptions."
    //
    //   AddSingleton<IConfigureOptions<TOptions>, TConfigureOptions>()
    //       "and here is the class that actually fills in a TOptions from that TIdentityProvider."
    //
    // Register the first without the second and you get a scheme that resolves but is configured with
    // nothing — an OIDC handler pointed at an empty Authority, which fails at the redirect with a message
    // that has no obvious connection to the missing line.
    public static IIdentityServerBuilder AddDynamicIdentityProviders(this IIdentityServerBuilder builder)
    {
        builder.Services.Configure<IdentityServerOptions>(options =>
        {
            options.DynamicProviders.AddProviderType<OpenIdConnectHandler, OpenIdConnectOptions, OpenIdConnectProvider>(
                IdentityProviderTypes.OpenIdConnect);
        });

        builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, OpenIdConnectConfigureOptions>();

        return builder;
    }
}
