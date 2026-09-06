namespace IdentityServerHost.Models.Constants;

// IdG counterpart: Models/Constants/IdentityProviderTypes.cs — verbatim, minus the four types this
// sample can't run. The string here is not decorative: it's the value stored in the IdentityProviders
// table's Type column, and it's the key IdentityProviderStore.MapIdp switches on to decide which
// strongly-typed provider class a database row becomes. Get it wrong by one character and the row maps
// to nothing and the scheme silently doesn't exist.
public static class IdentityProviderTypes
{
    public const string OpenIdConnect = "openidconnect";

    // The real IdG also defines: azureadb2c, azuread, saml, guest. Each needs its own provider class, its
    // own ConfigureOptions class, and (for saml/guest) its own authentication handler — see
    // Configurations/Extensions/DynamicIdentityProviderExtensions.cs for where all five get registered
    // there and only one here. Not ported because this sample has exactly one runnable external IdP
    // (src/ExternalIdp), and it speaks plain OIDC.
}
