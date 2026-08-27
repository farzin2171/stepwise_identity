using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace IdentityServerHost.Controllers;

// The whole reason this project needed AddControllersWithViews(). Duende IdentityServer ships no UI of
// its own — IIdentityServerInteractionService is the contract between "the user needs to log in" (an
// authorize request IdentityServer can't complete) and "here is a page that can ask them to."
// IdG counterpart: Modules/Account/AccountController.cs (local-login half only — the external-provider
// half is ExternalController.cs, added in Phase 4).
public class AccountController(TestUserStore users, IIdentityServerInteractionService interaction, TenantContext tenantContext) : Controller
{
    [HttpGet]
    public IActionResult Login(string returnUrl) => View(new LoginViewModel
    {
        ReturnUrl = returnUrl,
        TenantDisplayName = tenantContext.DisplayName
    });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInputModel model)
    {
        if (users.ValidateCredentials(model.Username, model.Password))
        {
            var user = users.FindByUsername(model.Username);

            // The real IdG's EquisoftAuthorizeInteractionResponseGenerator does this cross-check at
            // TOKEN-ISSUANCE time (forcing a re-login on mismatch, unless the tenants are explicitly
            // linked via LinkedTenants). This sample checks it here instead, at credential-submission
            // time — same idea (an authenticated principal from tenant A can't complete a login that
            // asked for tenant B), simpler mechanics, no linking escape hatch.
            var requiredTenant = Tenants.ResolveTenantKey(model.ReturnUrl);
            var usersTenant = user.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
            if (requiredTenant is not null && usersTenant != requiredTenant)
            {
                ModelState.AddModelError(string.Empty,
                    $"{model.Username} does not belong to {Tenants.DisplayNames[requiredTenant]}.");
                return View(new LoginViewModel
                {
                    ReturnUrl = model.ReturnUrl,
                    TenantDisplayName = tenantContext.DisplayName
                });
            }

            // IdentityServerUser wraps the claims IdentityServer itself needs (sub, idp, auth time).
            // Everything else — name, email, tenant_id — comes from the default TestUser-backed profile
            // service, which returns every claim on the TestUser matching a requested scope's UserClaims.
            await HttpContext.SignInAsync(new IdentityServerUser(user.SubjectId)
            {
                DisplayName = user.Username,
                IdentityProvider = IdentityServerConstants.LocalIdentityProvider
            });

            // Resumes the /connect/authorize request that redirected here in the first place — signing in
            // above didn't finish the OIDC flow, it just made this redirect valid.
            if (Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return Redirect("~/");
        }

        ModelState.AddModelError(string.Empty, "Invalid username or password");
        return View(new LoginViewModel { ReturnUrl = model.ReturnUrl, TenantDisplayName = tenantContext.DisplayName });
    }

    [HttpGet]
    public async Task<IActionResult> Logout(string logoutId, CancellationToken ct)
    {
        await HttpContext.SignOutAsync();
        var logoutRequest = await interaction.GetLogoutContextAsync(logoutId, ct);
        return Redirect(logoutRequest.PostLogoutRedirectUri ?? "~/");
    }
}

public class LoginViewModel
{
    public string? ReturnUrl { get; set; }
    public string? TenantDisplayName { get; set; }
}

public class LoginInputModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
