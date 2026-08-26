using Microsoft.AspNetCore.Authentication.JwtBearer;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           // Where to find the discovery document + JWKS. The middleware fetches
           // /.well-known/openid-configuration from here once (then caches it), reads jwks_uri from it,
           // and validates every incoming token's signature against those keys — no shared secret, no
           // per-request round trip back to IdentityServerHost.
           options.Authority = "http://localhost:5000";

           // Local teaching sample only: the Authority above is plain HTTP. A real API requires HTTPS
           // everywhere — never disable this in real code.
           options.RequireHttpsMetadata = false;

           // Must match the ApiResource name in IdentityServerHost/Config.cs — Duende stamps that name
           // into the token's "aud" claim. A token issued for a different audience is rejected here
           // before this API's own code ever runs.
           options.TokenValidationParameters.ValidAudience = "api1";

           options.MapInboundClaims = false;
       });

builder.Services.AddAuthorization(options =>
{
    // [Authorize] alone only checks "is this token valid" — it says nothing about what the token was
    // issued for. This policy additionally requires the "api1" scope claim, so a valid token minted for
    // some other API (with no "api1" scope) still gets refused here.
    options.AddPolicy("ApiScope", policy => policy.RequireClaim("scope", "api1"));
});

// MvcClient never needed this — it calls this API from a server-to-server HttpClient, not from a
// browser. ReactSpa calls it with the browser's own fetch(), from a different origin (:5173 vs :5003),
// which makes this a CORS request: without an explicit allow-list, the browser blocks the preflight
// before the real GET (with its Authorization header) is ever sent.
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactSpa", policy => policy
        .WithOrigins("http://localhost:5173")
        .AllowAnyMethod()
        .AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("ReactSpa");
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/identity", (HttpContext ctx) => Results.Ok(new
{
    message = "Hello from SampleApi — you only see this because your access token passed signature, " +
              "expiry, issuer, audience, and scope validation.",
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("ApiScope");

app.Run();
