using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Mini.Infrastructure.ExternalServices;
using Mini.Infrastructure.Identity;
using Mini.Infrastructure.MultiTenant;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;

namespace AgentPortal.Controllers;

public class HomeController(
    IAuthorizationClient authorizationClient,
    ITenantContext tenantContext,
    IIdentityContext identityContext) : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    [RequireTenant]
    public IActionResult Secure() => View((User.Claims, tenantContext.Tenant));

    // Phase 18. Gives Agent Portal a reason to exist beyond proving login works: an authorized action
    // that calls Mini.AuthorizationService through Mini.Infrastructure's shared AuthorizationClient (the
    // same client SampleApi's own "/authorize/{resourceName}" endpoint uses, extracted in Phase 16).
    // Mirrors MvcClient's HomeController.CallApi() — forward the signed-in user's own access token,
    // don't fetch a fresh one — but calls a DIFFERENT resource ("agent-portal", not "sample-api") so the
    // seeded policies actually distinguish the two consumers instead of coincidentally agreeing.
    [Authorize]
    [RequireTenant]
    public async Task<IActionResult> CheckAuthorization()
    {
        // SaveTokens = true (Program.cs) is what makes this token available here — same reason
        // MvcClient's CallApi() can call HttpContext.GetTokenAsync without a fresh round trip.
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (accessToken is null)
        {
            return View("AuthorizationResult", new AuthorizationResult(false, "No access token found on the current session — sign out and back in."));
        }

        // Same context shape SampleApi's /authorize/{resourceName} endpoint builds: role (for the
        // authorization service to compare against its own decision) and caller (User vs. Service —
        // always User here, since this action requires [Authorize] on a browser-based cookie session).
        var roleFromToken = User.Claims.FirstOrDefault(c => c.Type == "role")?.Value ?? "Member";
        var context = new Dictionary<string, string>
        {
            { "role", roleFromToken },
            { "caller", identityContext.IdentityType.ToString() }
        };

        var result = await authorizationClient.EvaluateAsync("agent-portal", identityContext, accessToken, context);
        return View("AuthorizationResult", result);
    }
}
