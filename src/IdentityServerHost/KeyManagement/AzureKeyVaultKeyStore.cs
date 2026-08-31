using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Certificates;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Stores;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IdentityServerHost.KeyManagement;

// IdG counterpart: IdentityServer/Stores/AzureKeyVaultKeyStore.cs — same shape, one class
// implementing both Duende interfaces, backed by the same Azure.Security.KeyVault.Certificates
// CertificateClient. Registered as a singleton for BOTH interfaces (see SigningKeyExtensions.cs) so
// there's exactly one CertificateClient and one shared cache entry, not two independent ones.
public class AzureKeyVaultKeyStore : ISigningCredentialStore, IValidationKeysStore
{
    private const string CacheKey = "IdentityServerSigningKeys";

    private readonly CertificateClient _certificateClient;
    private readonly AzureKeyVaultOptions _options;
    private readonly IMemoryCache _cache;

    public AzureKeyVaultKeyStore(IOptions<AzureKeyVaultOptions> options, IMemoryCache cache)
    {
        _options = options.Value;
        _cache = cache;

        TokenCredential credential = string.IsNullOrEmpty(_options.ClientSecret)
            ? new DefaultAzureCredential()
            : new ClientSecretCredential(_options.TenantId, _options.ClientId, _options.ClientSecret);
        _certificateClient = new CertificateClient(new Uri($"https://{_options.VaultName}.vault.azure.net"), credential);
    }

    public async Task<SigningCredentials> GetSigningCredentialsAsync(CancellationToken ct)
    {
        var (signingCredential, _) = await GetOrLoadKeysAsync(ct);
        return signingCredential;
    }

    public async Task<IReadOnlyCollection<SecurityKeyInfo>> GetValidationKeysAsync(CancellationToken ct)
    {
        var (_, validationKeys) = await GetOrLoadKeysAsync(ct);
        return validationKeys;
    }

    private Task<(SigningCredentials SigningCredential, IReadOnlyCollection<SecurityKeyInfo> ValidationKeys)> GetOrLoadKeysAsync(CancellationToken ct) =>
        _cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(_options.RefreshIntervalHours);
            return await LoadKeysFromVaultAsync(ct);
        });

    private async Task<(SigningCredentials, IReadOnlyCollection<SecurityKeyInfo>)> LoadKeysFromVaultAsync(CancellationToken ct)
    {
        // CertificateProperties.NotBefore/ExpiresOn are DateTimeOffset, always UTC — unlike
        // X509Certificate2.NotBefore/NotAfter, which are DateTime in the LOCAL time zone. Comparing
        // against these properties instead of the downloaded certificate's own fields sidesteps that
        // well-known .NET gotcha entirely, rather than needing a ToUniversalTime() everywhere.
        var candidates = new List<CertificateProperties>();
        await foreach (var props in _certificateClient.GetPropertiesOfCertificateVersionsAsync(_options.CertificateName, ct))
        {
            if (props.Enabled != true)
            {
                continue;
            }

            if (props.ExpiresOn is { } expiresOn && expiresOn < DateTimeOffset.UtcNow)
            {
                continue;
            }

            candidates.Add(props);
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No enabled, non-expired versions of certificate '{_options.CertificateName}' found in vault '{_options.VaultName}'.");
        }

        // The active SIGNING version: the newest one old enough to have cleared the rollover delay.
        // Every candidate — including newer versions not yet old enough to sign with — still becomes
        // a VALIDATION key below, so tokens signed moments ago with the previous version keep validating
        // right through the rollover, and a brand-new version is already advertised before it's ever used.
        var rolloverCutoff = DateTimeOffset.UtcNow.AddHours(-_options.RolloverDelayHours);
        var signingVersion = candidates
            .Where(c => c.NotBefore <= rolloverCutoff)
            .OrderByDescending(c => c.NotBefore)
            .FirstOrDefault() ?? candidates.OrderByDescending(c => c.NotBefore).First();

        var validationKeys = new List<SecurityKeyInfo>();
        X509Certificate2? signingCertificate = null;
        foreach (var candidate in candidates)
        {
            var downloaded = await _certificateClient.DownloadCertificateAsync(_options.CertificateName, candidate.Version, ct);
            validationKeys.Add(new SecurityKeyInfo
            {
                Key = new X509SecurityKey(downloaded.Value),
                SigningAlgorithm = SecurityAlgorithms.RsaSha256
            });

            if (candidate.Version == signingVersion.Version)
            {
                signingCertificate = downloaded.Value;
            }
        }

        var signingCredential = new SigningCredentials(new X509SecurityKey(signingCertificate!), SecurityAlgorithms.RsaSha256);
        return (signingCredential, validationKeys);
    }
}
