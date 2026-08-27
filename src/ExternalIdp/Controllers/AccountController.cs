using Duende.IdentityServer;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Test;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace ExternalIdp.Controllers;

// Identical shape to IdentityServerHost's own AccountController — every Duende IdentityServer needs
// this, including the one standing in as an external IdP here.
public class AccountController(TestUserStore users, IIdentityServerInteractionService interaction) : Controller
{
    [HttpGet]
    public IActionResult Login(string returnUrl) => View(new LoginViewModel { ReturnUrl = returnUrl });

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginInputModel model)
    {
        if (users.ValidateCredentials(model.Username, model.Password))
        {
            var user = users.FindByUsername(model.Username);

            await HttpContext.SignInAsync(new IdentityServerUser(user.SubjectId)
            {
                DisplayName = user.Username,
                IdentityProvider = IdentityServerConstants.LocalIdentityProvider
            });

            if (Url.IsLocalUrl(model.ReturnUrl))
            {
                return Redirect(model.ReturnUrl);
            }

            return Redirect("~/");
        }

        ModelState.AddModelError(string.Empty, "Invalid username or password");
        return View(new LoginViewModel { ReturnUrl = model.ReturnUrl });
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
}

public class LoginInputModel
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
