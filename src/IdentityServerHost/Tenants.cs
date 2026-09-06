using Microsoft.AspNetCore.WebUtilities;

namespace IdentityServerHost;

// In-memory equivalent of the real IdG's Tenant service lookup (TenantClient.GetTenantAsync) — a
// name -> tenant record resolver. The real one is an HTTP call to an external service, with its result
// cached forever (EquisoftTokenResponseGenerator.AddTenantIdToPayloadAsync, AbsoluteExpiration =
// DateTimeOffset.MaxValue — a known ops risk, out of scope for this sample: there's no cache here at all,
// so the bug can't reproduce, but its absence shouldn't read as "this sample proves it's fine").
public static class Tenants
{
    public static IReadOnlyDictionary<string, string> DisplayNames => new Dictionary<string, string>
    {
        ["acme"] = "Acme Corp",
        ["globex"] = "Globex Corporation",
        // Phase 9. Initech has no local test user and no entry under "ExternalProviders" in
        // appsettings.json — its only way in is a database-backed identity provider row. That makes it the
        // control case for this phase: if Initech's login page offers an external option, it can only have
        // come from the IdentityProviders table.
        //
        // Be precise about what this does and doesn't demonstrate. Onboarding Initech still needed this
        // line, because tenant display names are a hardcoded dictionary here (Phase 3's design). What
        // moved into the database is the *provider* configuration, not the tenant. The real IdG resolves
        // tenants through a service call instead — see TenantClient (Phase 7) for the piece this sample
        // does port.
        ["initech"] = "Initech"
    };

    // Which external providers show up on which tenant's login page used to be a hardcoded dictionary
    // here (Phase 4's first cut). It's now Configurations/Authentication/Helpers/AuthenticationHelper —
    // each provider declares its own EcosystemTenant in config, the same way the real IdG's providers do,
    // instead of a second mapping that could drift out of sync with the provider list itself.

    // Finds the IdG's acr_values=tenant:<name> hint inside a URL's own query string, or (if the URL is a
    // login page's ReturnUrl) inside the original request it has re-encoded. Handles both
    // /connect/authorize?acr_values=... directly and /Account/Login?ReturnUrl=....
    //
    // acr_values is a direct query parameter on /connect/authorize, but by the time IdentityServer has
    // redirected to /Account/Login?ReturnUrl=..., it isn't a top-level parameter anymore — Duende
    // re-encodes the entire original request inside ReturnUrl and hands that to the login page instead.
    // The real AuthenticationHelper gets this for free by asking
    // IIdentityServerInteractionService.GetAuthorizationContextAsync(returnUrl) instead of parsing raw
    // query strings — a real API this sample deliberately avoids so the underlying convention stays visible.
    public static string? ResolveTenantKey(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var acrValues = ExtractQueryValue(url, "acr_values");
        if (acrValues is null)
        {
            var nested = ExtractQueryValue(url, "ReturnUrl");
            if (nested is not null) acrValues = ExtractQueryValue(Uri.UnescapeDataString(nested), "acr_values");
        }

        var hint = acrValues?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(v => v.StartsWith("tenant:", StringComparison.OrdinalIgnoreCase))
            ?[7..];

        return hint is not null && DisplayNames.ContainsKey(hint) ? hint : null;
    }

    private static string? ExtractQueryValue(string url, string key)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return null;
        var query = QueryHelpers.ParseQuery(url[queryStart..]);
        return query.TryGetValue(key, out var values) ? values.ToString() : null;
    }
}
