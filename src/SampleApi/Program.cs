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

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/identity", (HttpContext ctx) => Results.Ok(new
{
    message = "Hello from SampleApi — you only see this because your access token passed signature, " +
              "expiry, issuer, audience, and scope validation.",
    claims = ctx.User.Claims.Select(c => new { c.Type, c.Value })
})).RequireAuthorization("ApiScope");

app.Run();
