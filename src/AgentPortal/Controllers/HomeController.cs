using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentPortal.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    // No LoginAsTenant()-equivalent here — Phase 17 is a skeleton proving a second client can log in
    // against IdentityServerHost with its own registration. Tenant-aware login (MvcClient's
    // LoginAsTenant()/acr_values pattern) is a feature this project hasn't grown yet, not a missing
    // requirement of this phase. See README.md's "Where this sample simplifies".
    [Authorize]
    public IActionResult Secure() => View(User.Claims);
}
