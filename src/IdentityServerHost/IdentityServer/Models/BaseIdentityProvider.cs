using System.Text.Json;
using Duende.IdentityServer.Models;
using IdentityServerHost.Configurations.Authentication;

namespace IdentityServerHost.IdentityServer.Models;

// IdG counterpart: IdentityServer/Models/BaseIdentityProvider.cs.
//
// This class is the whole point of Phase 9, and it is worth reading slowly. It inherits Duende's
// IdentityProvider (a database row: Scheme, DisplayName, Type, Enabled, and a free-form Properties
// dictionary) and implements IAuthenticationOptions — the SAME interface BaseAuthenticationOptions
// implements for the file-based providers Phase 4 introduced.
//
// That shared interface is the seam. AuthenticationHelper, AccountController, and anything else that
// asks "which external providers can this tenant use?" keep working against IAuthenticationOptions and
// never learn whether a given provider came from appsettings.json or from a row in the IdentityProviders
// table. Two completely different storage mechanisms, one contract.
//
// The mechanical trick: Duende gives every IdentityProvider a string-keyed Properties bag, exposed
// through the this["Key"] indexer. The strongly-typed properties below are just readers over that bag —
// which is why a database-backed provider can carry arbitrary per-provider settings without a schema
// change, and also why a typo in a property name fails silently as null rather than loudly at startup.
// That trade-off is real and the real IdG lives with it too.
//
// A `record`, not a `class` — and not by preference. Duende made IdentityProvider a record in v8, and
// "only records may inherit from records" (CS8865), so this sample's port can't match the real IdG's
// `class` declaration line-for-line even though every other line is near-verbatim. Worth knowing what
// this quietly changes: records get value-based equality, so two BaseIdentityProvider instances built
// from the same row now compare equal where the real IdG's would not. Nothing here depends on that
// today, but it's the kind of difference that surprises you later.
public abstract record BaseIdentityProvider : IdentityProvider, IAuthenticationOptions
{
    // Duende calls it Scheme; the sample's IAuthenticationOptions (written in Phase 4, before any of this
    // existed) calls it Name. Explicit interface implementation bridges the two without shadowing
    // Duende's own property, which the dynamic-provider infrastructure reads by its real name.
    string IAuthenticationOptions.Name => Scheme;

    // Same bridge, different reason: Duende's inherited DisplayName is nullable, the interface's is not.
    // Falling back to the scheme name means a row with no DisplayName renders a usable (if ugly) login
    // button instead of a blank one.
    string IAuthenticationOptions.DisplayName => DisplayName ?? Scheme;

    // Phase 4 put EcosystemTenant on the file-based provider as a first-class config field. Here it's a
    // Properties entry — but note it IS an explicit property in the real IdG's database rows too (see
    // config/identityProviders.json in the real repo, where every entry carries "EcosystemTenant").
    // CONTEXT.md previously claimed the real IdG derives the tenant from the scheme NAME instead; that's
    // true only of the file-based path's "{Name}_{SchemeDefault}" convention, not of database rows.
    public string EcosystemTenant => this["EcosystemTenant"] ?? string.Empty;

    // Still modeled-but-not-consumed, exactly as on the file-based side — see IAuthenticationOptions.
    // The real IdG deserializes these two with Newtonsoft; this sample has no Newtonsoft reference and
    // uses System.Text.Json. Same result for these shapes, one less dependency.
    public FederatedConfigurationOptions? FederatedConfiguration =>
        this["FederatedConfiguration"] is { } json ? JsonSerializer.Deserialize<FederatedConfigurationOptions>(json) : null;

    public IDictionary<string, string> ClaimMappings =>
        this["ClaimMappings"] is { } json
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();

    protected BaseIdentityProvider(string type) : base(type) { }

    protected BaseIdentityProvider(string type, IdentityProvider other) : base(type, other) { }
}
