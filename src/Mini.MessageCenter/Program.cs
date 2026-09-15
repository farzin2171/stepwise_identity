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

// THROWAWAY diagnostic endpoint, documented as such: Phase 21 is what actually publishes
// PolicyChangedEvent from Mini.AuthorizationService when a Policy row changes. Until then, nothing in
// production code calls Publish at all, so there is no real trigger to drive test-phase20.ps1 against
// - this endpoint exists purely so the phase can prove the consumer + webhook-delivery path works,
// the same way Phase 13's test harness proved Mini.AuthorizationService before Phase 14 wired a real
// caller into it. Remove this endpoint once Phase 21 ships a real publisher.
app.MapPost("/api/v1/test/publish-policy-changed", async (PublishTestEventRequest request, IPublishEndpoint publishEndpoint) =>
{
    var evt = new PolicyChangedEvent
    {
        TenantKey = request.TenantKey,
        ResourceName = request.ResourceName ?? "sample-api",
        OldCondition = request.OldCondition,
        NewCondition = request.NewCondition ?? """{"requiredRoles": ["Admin"]}""",
        ChangedAtUtc = DateTimeOffset.UtcNow
    };

    await publishEndpoint.Publish(evt);

    return Results.Accepted(value: evt);
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();

internal record PublishTestEventRequest(string TenantKey, string? ResourceName, string? OldCondition, string? NewCondition);
