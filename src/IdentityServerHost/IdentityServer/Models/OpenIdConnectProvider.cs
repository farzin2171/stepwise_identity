using Duende.IdentityServer.Models;
using IdentityServerHost.Configurations.Authentication.OpenId;
using IdentityServerHost.Models.Constants;

namespace IdentityServerHost.IdentityServer.Models;

// IdG counterpart: IdentityServer/Models/OpenIdConnectProvider.cs.
//
// The database-backed twin of Configurations/Authentication/OpenId/OpenIdConnectProviderOptions.cs.
// Compare the two side by side — they carry the same settings for the same purpose, but one is a POCO
// the options binder fills from appsettings.json, and this one reads each value out of the inherited
// Properties bag on demand. Neither knows the other exists.
//
// Note what ISN'T here that OpenIdConnectProviderOptions has: CallbackPath. A dynamic provider doesn't
// get to choose its callback path — Duende's dynamic-provider infrastructure owns the URL space under
// /federation/{scheme}/ and hands the computed path to ConfigureOptions. That's a genuine
// different-in-kind difference from the file-based path, not a simplification: with file-based providers
// you register a redirect URI with the IdP and configure it here; with dynamic providers the path is
// derived from the scheme name and you register whatever Duende decides.
public record OpenIdConnectProvider : BaseIdentityProvider, IOpenIdConnectConfigurationOptions
{
    public string Authority => this["Authority"] ?? string.Empty;

    public string ClientId => this["ClientId"] ?? string.Empty;

    public string ClientSecret => this["ClientSecret"] ?? string.Empty;

    // Comma-separated in the Properties bag, because the bag is string-to-string and has nowhere to put
    // an array. The file-based side gets a real string[] straight from the config binder — the same
    // information, one flattening step apart.
    public string[] Scopes =>
        this["Scopes"] is { Length: > 0 } scopes ? scopes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : [];

    // Defaults to true when absent, matching both OpenIdConnectProviderOptions' default and the real
    // IdG's own `this["GetClaimsFromUserInfoEndpoint"] == null || bool.Parse(...)`.
    public bool GetClaimsFromUserInfoEndpoint =>
        this["GetClaimsFromUserInfoEndpoint"] is not { } value || !bool.TryParse(value, out var parsed) || parsed;

    public OpenIdConnectProvider() : base(IdentityProviderTypes.OpenIdConnect) { }

    public OpenIdConnectProvider(IdentityProvider other) : base(IdentityProviderTypes.OpenIdConnect, other) { }
}
