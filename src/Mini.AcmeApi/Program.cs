using Microsoft.AspNetCore.Authentication.JwtBearer;

// Acme Corporation's OWN user API — a third-party system this repo does not otherwise own, standing in
// for the "custom web APIs" half of the real Services.User's per-tenant connector chain (see the
// dit-architecture skill's user-service.md: "Custom web APIs — IWebApiConnector: per-tenant/per-handler
// endpoints and parameterized routes; bearer auth via a service-account token; can inject an
// OriginUserIdentifier header for user context").
//
// Nothing in this repo calls it directly. Mini.UserService reaches it through a WebApi *connector*
// whose host and routes are rows in SQL, not config in either process — which is the whole point of
// Phase 12: adding a tenant's own user source is a data change, not a code change.
//
// Read this file as "a system belonging to somebody else." It has no idea what a tenant is, no
// Tenants table, and no notion of the three registries described in docs/architecture/README.md. It
// knows two users, because Acme's HR system knows two users.
var builder = WebApplication.CreateBuilder(args);

var authentication = builder.Configuration.GetSection("Authentication");

// Acme trusts the DIT platform's issuer — same JWKS, same discovery document every other API in this
// sample validates against. That is what makes this a *connector* target rather than an integration
// needing its own credential exchange.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(options =>
       {
           options.Authority = authentication["Authority"];
           options.RequireHttpsMetadata = false;
           options.TokenValidationParameters.ValidAudiences = [authentication["Audience"]!];

           // Same reason Mini.UserService and SampleApi set it: without this, "client_id" is fine but
           // "sub" arrives as a long URI. Kept for consistency, since the policy below reads raw claim
           // names.
           options.MapInboundClaims = false;
       });

builder.Services.AddAuthorization(options =>
{
    // Trusting the issuer is NOT the same as trusting every tenant on it, and this policy is where
    // that distinction lives. All three of Mini.UserService's service accounts
    // (userservice-svc.{acme,globex,initech}) can request the "acmeapi" scope and so all three get a
    // token with the right audience — but only Acme's own gets past here.
    //
    // That is not defensive decoration. It is what makes Initech's deliberately mis-pointed connector
    // row (its WebApiConnectorConfiguration.Host names THIS host) fail with a 403 instead of quietly
    // serving Initech users out of Acme's HR system. A per-tenant connector configuration that names
    // the wrong tenant's host is the single most likely connector misconfiguration there is, and this
    // is the only thing in the whole chain positioned to catch it.
    options.AddPolicy("AcmeServiceAccount", policy => policy
        .RequireClaim("client_id", authentication["AllowedClientId"]!));
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Acme's own employee directory. Note what the roles are: alice is "Admin", byte-identical to what
// Mini.UserService's own UserIdentityRoles table says about her — deliberately, so test-phase7.ps1
// keeps passing while the SOURCE of that claim changes underneath it. The same trick Phase 11 played
// with the tenant GUIDs, for the same reason: a replacement is only provable when the value is held
// constant and the mechanism moves.
//
// carol is the new information. She is a federated identity (external:external-idp:ext-1) with no row
// in MiniUsers at all, so before this phase she got the "Member" fallback. Acme's own system knows
// she is an Underwriter, and now that answer reaches her token.
var usersById = new Dictionary<string, AcmeUser>
{
    ["1"] = new("1", "Alice Anderson", "alice@acme.test", "Admin"),
    ["external:external-idp:ext-1"] = new("external:external-idp:ext-1", "Carol Carter", "carol@acme.test", "Underwriter")
};

var protectedApi = app.MapGroup("/users").RequireAuthorization("AcmeServiceAccount");

// Route template, from Acme's point of view. Mini.UserService does not know this string: it lives in
// a WebApiConnectorConfigurationRoutes row ("users/{id}") in the MiniUsers database, and changing it
// there is how you would follow Acme renaming this route.
protectedApi.MapGet("/{userId}", (string userId, HttpContext ctx, ILogger<Program> logger) =>
{
    // The real IWebApiConnector "can inject an OriginUserIdentifier header for user context." Acme's
    // system does nothing with it except log it, which is honest: the header exists so the tenant's
    // own audit trail can record which end user a platform service account was acting for. Without
    // it every connector call looks like "userservice-svc.acme did something," which is useless to
    // whoever has to answer "who looked at this record."
    var originUser = ctx.Request.Headers["OriginUserIdentifier"].FirstOrDefault();
    logger.LogInformation("Acme user lookup for {UserId} (OriginUserIdentifier: {OriginUser})", userId, originUser ?? "<none>");

    return usersById.TryGetValue(userId, out var user)
        ? Results.Ok(new { user.UserId, user.DisplayName, user.Email, user.Role, originUserIdentifier = originUser })
        : Results.NotFound();
});

// The second extension point, and the one that proves a handler can be non-cascading. Real
// counterpart: Services.User's GET /user/identities/email?email=… , which resolves a user by email
// through the same connector machinery as the role lookup.
protectedApi.MapGet("/by-email/{email}", (string email) =>
{
    var user = usersById.Values.FirstOrDefault(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
    return user is null
        ? Results.NotFound()
        : Results.Ok(new { user.UserId, user.DisplayName, user.Email, user.Role });
});

// Phase 20: Acme's inbound webhook receiver for Mini.MessageCenter's acme-scoped subscription. Left
// UNAUTHENTICATED and UNVERIFIED on purpose - this file already carries none of this repo's platform
// conventions (see the file banner above), and touching it to add HMAC verification would suggest
// Acme's own system participates in that mechanism, which it doesn't in this sample. This IS a real
// gap worth naming plainly: nothing stops an arbitrary caller from POSTing a fake policy-changed
// payload here. Mini.MessageCenter signs every delivery anyway (see its README's "Delivery signing"
// section) so the mechanism exists; Acme's stand-in simply doesn't check it, unlike
// WebhookReceiverStub, which does.
app.MapPost("/webhooks/policy-changed", (HttpContext ctx, ILogger<Program> logger) =>
{
    var signature = ctx.Request.Headers["X-Webhook-Signature"].FirstOrDefault();
    logger.LogInformation("Acme received a policy-changed webhook (signature present: {HasSignature}).", signature is not null);
    return Results.Ok(new { received = true });
});

// Unauthenticated liveness probe, so run-all.ps1 can tell "listening and finished starting" from
// "port is open but still warming up." Same shape as every other /health in this repo.
app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();

internal record AcmeUser(string UserId, string DisplayName, string Email, string Role);
