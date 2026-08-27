using System.Security.Claims;
using Duende.IdentityServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace IdentityServerHost.Controllers;

// The protocol-agnostic external login callback — every external scheme (just "external-idp" here; a
// real deployment might have several) converges on the same two actions. IdG counterpart:
// Modules/Account/ExternalController.cs. One divergence worth calling out: tenant_id comes from the
// tenant this login was FOR (round-tripped through AuthenticationProperties), not from a per-scheme
// EcosystemTenant config value the way the real ITenantAccessor resolves it. That's a legitimate
// difference in shape, not just a simplification: this sample's tenant is a property of the *request*,
// the real IdG's is a property of the *scheme*.
public class ExternalController(TenantContext tenantContext, ExternalUserStore externalUsers) : Controller
{
    [HttpGet]
    public IActionResult Challenge(string scheme, string returnUrl)
    {
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Callback)),
            Items =
            {
                ["returnUrl"] = returnUrl,
                // The external IdP has no concept of "acme" or "globex" — this is the one piece of local
                // context that has to survive the round trip through it, carried in the encrypted state
                // parameter the same way returnUrl is.
                ["tenant"] = tenantContext.TenantKey
            }
        };

        return base.Challenge(props, scheme);
    }

    [HttpGet]
    public async Task<IActionResult> Callback()
    {
        var result = await HttpContext.AuthenticateAsync(IdentityServerConstants.ExternalCookieAuthenticationScheme);
        if (!result.Succeeded)
        {
            return BadRequest("External authentication did not succeed.");
        }

        var externalSubjectId = result.Principal!.FindFirstValue("sub");
        var name = result.Principal.FindFirstValue("name");
        var tenantKey = result.Properties!.Items["tenant"];
        var returnUrl = result.Properties.Items["returnUrl"];

        await HttpContext.SignOutAsync(IdentityServerConstants.ExternalCookieAuthenticationScheme);

        // Claim transformation: the external IdP's claims never reach IdentityServer's own principal
        // as-is. "sub" here is ExternalIdp's own subject id (ext-1, meaningless to us) — the local
        // identity is (scheme, externalSubjectId), same pairing concept as the real UserStore's
        // FindByExternalProviderAsync((ProviderName, ProviderSubjectId)).
        var localSubjectId = $"external:external-idp:{externalSubjectId}";

        // Persisted separately from the principal below because IProfileService's context.Subject can't
        // see these claims later (see ExternalUserStore.cs) — this is the "first-login provisioning" step.
        await externalUsers.ProvisionAsync(localSubjectId, [new Claim("name", name!), new Claim("tenant_id", tenantKey!)]);

        await HttpContext.SignInAsync(new IdentityServerUser(localSubjectId)
        {
            DisplayName = name,
            IdentityProvider = "external-idp"
        });

        if (Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return Redirect("~/");
    }
}
