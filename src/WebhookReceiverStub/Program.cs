using System.Security.Cryptography;
using System.Text;

// A minimal testing aid, in the same spirit as ExternalServicesStub (see CONTEXT.md): its only job is
// to receive webhooks Mini.MessageCenter sends to its unscoped subscription, verify the HMAC
// signature, log the payload, and let a test script poll what it received. Not shaped like a
// production service on purpose — one file, in-memory storage, no database, no auth beyond the
// signature check.
var builder = WebApplication.CreateBuilder(args);
var secret = builder.Configuration["Webhook:Secret"]!;

var app = builder.Build();

var received = new List<ReceivedWebhook>();
var maxRetained = 50;

app.MapPost("/webhook", async (HttpRequest request, ILogger<Program> logger) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    var signatureHeader = request.Headers["X-Webhook-Signature"].FirstOrDefault();
    var expectedSignature = ComputeSignature(body, secret);
    // Constant-time compare, the same reason any HMAC verification uses one: a naive == leaks how
    // many leading bytes matched via response timing.
    var signatureValid = signatureHeader is not null &&
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(signatureHeader),
            Convert.FromHexString(expectedSignature));

    var entry = new ReceivedWebhook(DateTime.UtcNow, body, signatureHeader, signatureValid);
    received.Insert(0, entry);
    if (received.Count > maxRetained)
    {
        received.RemoveRange(maxRetained, received.Count - maxRetained);
    }

    logger.LogInformation(
        "Webhook received. SignatureValid={SignatureValid} Body={Body}", signatureValid, body);

    return Results.Ok(new { received = true, signatureValid });
});

// Inspection endpoint a test script polls — the whole reason this stub exists rather than a real
// receiver logging to a file nobody automated can read.
app.MapGet("/webhook/received", () => Results.Ok(received));

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();

app.Run();

static string ComputeSignature(string payloadJson, string secret)
{
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
    return Convert.ToHexString(hash).ToLowerInvariant();
}

internal record ReceivedWebhook(DateTime ReceivedAtUtc, string Body, string? Signature, bool SignatureValid);
