using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mini.Infrastructure.Http;
using Mini.MessageCenter.Data;

namespace Mini.MessageCenter.Webhooks;

/// <summary>
/// Signs and POSTs one webhook payload, with Polly retry, and records a <see cref="DeliveryAttempt"/>
/// whatever the outcome — this course's own invention, not a port (see this project's README and
/// CONTEXT.md's "Mini.MessageCenter" entry: neither Libraries.Infrastructure nor the real
/// Services.MessageCenter have a subscription/signing/retry concept to copy).
/// </summary>
public class WebhookDeliveryService
{
    // Same header name convention as this repo's other bespoke headers (OriginUserIdentifier) —
    // an X-prefixed, PascalCase custom header, documented here since there is no real counterpart
    // to point at for the name itself.
    public const string SignatureHeader = "X-Webhook-Signature";

    private readonly HttpClient _httpClient;
    private readonly MessageCenterDbContext _db;
    private readonly ILogger<WebhookDeliveryService> _logger;

    public WebhookDeliveryService(HttpClient httpClient, MessageCenterDbContext db, ILogger<WebhookDeliveryService> logger)
    {
        _httpClient = httpClient;
        _db = db;
        _logger = logger;
    }

    public async Task DeliverAsync(WebhookSubscription subscription, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        var signature = ComputeSignature(json, subscription.Secret);

        var attempt = new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            SubscriptionId = subscription.Id,
            EventSummary = json,
            AttemptedAtUtc = DateTime.UtcNow
        };

        try
        {
            // Retry is Mini.Infrastructure's own ResiliencePolicies.Retry() — two attempts after the
            // first failure, 2s then 4s. No circuit breaker here: each delivery is to a different
            // subscription and infrequent enough that a breaker tripped by one bad subscriber would
            // only mask the next event's delivery to it, with no shared HttpClient instance across
            // calls to make the breaker's shared state meaningful anyway.
            var response = await ResiliencePolicies.Retry().ExecuteAsync(async () =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, subscription.CallbackUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                request.Headers.Add(SignatureHeader, signature);
                return await _httpClient.SendAsync(request, ct);
            });

            attempt.Success = response.IsSuccessStatusCode;
            attempt.ResponseStatusCode = (int)response.StatusCode;

            if (!attempt.Success)
            {
                _logger.LogWarning(
                    "Webhook delivery to {SubscriptionName} ({CallbackUrl}) returned {StatusCode}.",
                    subscription.Name, subscription.CallbackUrl, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // No dead-letter queue: after retry is exhausted, this is the end of the line. The
            // DeliveryAttempt row is the only record that it ever failed — see "Where this sample
            // simplifies" in the README.
            attempt.Success = false;
            attempt.Error = ex.Message;
            _logger.LogWarning(ex,
                "Webhook delivery to {SubscriptionName} ({CallbackUrl}) failed after retries.",
                subscription.Name, subscription.CallbackUrl);
        }

        _db.DeliveryAttempts.Add(attempt);
        await _db.SaveChangesAsync(ct);
    }

    public static string ComputeSignature(string payloadJson, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
