using Duende.IdentityServer.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace IdentityServerHost.KeyManagement;

// IdG counterpart: Configurations/Certificates/CertificatesExtensions.cs's AddCertificates() — a
// dispatcher, not a store itself. The real one branches on UseDeveloperSigningCredentials, then a
// three-way KeyManagementProvider enum (None|Azure|Local); this is simplified to the two ends of that
// spectrum this course actually needs. See KeyManagementOptions.cs for what's cut.
public static class SigningKeyExtensions
{
    public static IIdentityServerBuilder AddSigningKey(this IIdentityServerBuilder builder, IConfiguration configuration)
    {
        var options = configuration.GetSection("KeyManagement").Get<KeyManagementOptions>() ?? new KeyManagementOptions();

        if (!options.Provider.Equals("AzureKeyVault", StringComparison.OrdinalIgnoreCase))
        {
            return builder.AddDeveloperSigningCredential();
        }

        // No .AddSigningCredential<T>() exists on IIdentityServerBuilder for a custom store type — the
        // real AzureProvisioningExtension registers its store the same direct way, straight on
        // builder.Services. One singleton instance shared for both interfaces (see
        // AzureKeyVaultKeyStore.cs's own comment on why that matters, not two independent ones each
        // with their own CertificateClient and cache entry.
        builder.Services.AddMemoryCache();
        builder.Services.Configure<AzureKeyVaultOptions>(configuration.GetSection("KeyManagement:AzureKeyVault"));
        builder.Services.AddSingleton<AzureKeyVaultKeyStore>();
        builder.Services.AddSingleton<ISigningCredentialStore>(sp => sp.GetRequiredService<AzureKeyVaultKeyStore>());
        builder.Services.AddSingleton<IValidationKeysStore>(sp => sp.GetRequiredService<AzureKeyVaultKeyStore>());
        return builder;
    }
}
