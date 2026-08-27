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
        ["globex"] = "Globex Corporation"
    };

    // Real-IdG counterpart: a per-client Properties[tenantName] entry holding a comma-list of allowed
    // scheme names (AuthenticationHelper.GetAllAvailableIdentityProviders). Modeled per-tenant here
    // instead of per-client, since this sample only has one client family asking for external login.
    // Acme federates to the partner ExternalIdp; Globex has no external IdP configured at all and only
    // ever sees the local login form — the login page differs by tenant, not just by branding.
    public static IReadOnlyDictionary<string, string[]> AllowedExternalSchemes => new Dictionary<string, string[]>
    {
        ["acme"] = ["external-idp"],
        ["globex"] = []
    };

    public static IReadOnlyDictionary<string, string> SchemeDisplayNames => new Dictionary<string, string>
    {
        ["external-idp"] = "ExternalIdp (partner SSO)"
    };

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
