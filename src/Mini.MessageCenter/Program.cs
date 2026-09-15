using MassTransit;
using Microsoft.EntityFrameworkCore;
using Mini.Infrastructure.Messaging;
using Mini.MessageCenter.Data;
using Mini.MessageCenter.Messaging;
using Mini.MessageCenter.Webhooks;

// Phase 20: the first service downstream of Phase 19's message bus. Consumes PolicyChangedEvent and
// fans it out to webhook subscribers. See this project's README for the full "why," and CONTEXT.md's
// "Mini.MessageCenter" / "Webhook subscription" entries for what is and isn't a port.
var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("MiniMessageCenterDb");

builder.Services.AddDbContext<MessageCenterDbContext>(options =>
    options.UseSqlServer(connectionString, b => b.MigrationsAssembly("Mini.MessageCenter")));

builder.Services.AddHttpClient<WebhookDeliveryService>();

// AddMessageBus's configureConsumers callback (Phase 19's extension point, unused until now) is what
// lets this be the first real registration of a consumer against it.
builder.Services.AddMessageBus(builder.Configuration, x =>
{
    x.AddConsumer<PolicyChangedEventConsumer>();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MessageCenterDbContext>();
    db.Database.Migrate();
    MessageCenterDbContext.SeedData(db);
}

// Diagnostic-only read of delivery history, for test-phase20.ps1 to poll. No admin/subscription-
// management API exists yet (see README), and this endpoint carries no sensitive data beyond
// "did a webhook land" — same posture as Mini.UserService's /api/v2/identity diagnostic, except that
// one gates on a bearer token because it echoes the CALLER's own identity claims back to them; this
// one describes server-side delivery history with no caller-specific data in it, so there's nothing
// for a bearer token to scope by. Left AllowAnonymous rather than invented a new auth rule for it -
// see "Auth" in the README for the reasoning spelled out.
app.MapGet("/api/v1/deliveries", async (MessageCenterDbContext db) =>
{
    var deliveries = await db.DeliveryAttempts
        .OrderByDescending(d => d.AttemptedAtUtc)
        .Take(100)
        .ToListAsync();

    return Results.Ok(deliveries);
});

// Phase 21 removed the THROWAWAY diagnostic endpoint that used to live here
// (POST /api/v1/test/publish-policy-changed), exactly as it said it would when Phase 20 added it:
// Mini.AuthorizationService's new policy-admin API (PUT /api/v1/authorization/policies/{tenantKey}/{resourceName})
// is now the real trigger that publishes PolicyChangedEvent. test-phase21.ps1 drives that real path;
// test-phase20.ps1 is kept (this repo's convention is to keep superseded scripts, not delete them —
// see CONTEXT.md's "superseded" pattern) but can no longer run past its diagnostic-publish step, since
// the endpoint it called no longer exists. See docs/architecture/webhooks.md.

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();
