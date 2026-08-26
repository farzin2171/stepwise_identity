using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MvcClient.Controllers;

public class HomeController(IHttpClientFactory httpClientFactory) : Controller
{
    public IActionResult Index() => View();

    [Authorize]
    public IActionResult Secure() => View(User.Claims);

    // Demonstrates a client calling a protected API on the signed-in user's behalf: the access token
    // IdentityServerHost issued to THIS app (during login, because "api1" was in the requested scopes)
    // gets forwarded as a Bearer token. SampleApi never talks to IdentityServerHost or this app directly
    // to check it — it validates the token's signature, issuer, audience, and scope entirely on its own.
    [Authorize]
    public async Task<IActionResult> CallApi()
    {
        // SaveTokens = true (Program.cs) is what makes this token available here — it's stored inside
        // this app's own auth cookie alongside the claims, not fetched fresh on every request.
        var accessToken = await HttpContext.GetTokenAsync("access_token");
        if (accessToken is null)
        {
            return View("ApiResult", "No access token found on the current session — sign out and back in.");
        }

        var client = httpClientFactory.CreateClient("SampleApi");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/identity");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode && body.Length > 0)
        {
            using var parsed = JsonDocument.Parse(body);
            body = JsonSerializer.Serialize(parsed, new JsonSerializerOptions { WriteIndented = true });
        }

        return View("ApiResult", $"HTTP {(int)response.StatusCode} {response.StatusCode}\n\n{body}");
    }
}
