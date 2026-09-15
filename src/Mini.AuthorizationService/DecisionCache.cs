using System.Security.Cryptography;
using System.Text;

namespace Mini.AuthorizationService;

// Cache-aside logic for CachedDecision rows (see Data/AuthorizationDbContext.cs). Kept free of
// DbContext/HTTP concerns so the TTL/expiry rule — the one piece of branching logic Phase 15 adds —
// is table-tested directly, the same way Mini.UserService's connector chain is (see
// tests/StepwiseIdentity.Tests/ConnectorChainTests.cs).
public static class DecisionCache
{
    // Short enough that a policy change in the demo (edit a Policy row, hit evaluate again) is
    // visible within the length of a test script; long enough to actually demonstrate a cache hit
    // for a couple of back-to-back calls. The real Services.Authorization's Redis TTLs are a
    // deployment-configured value — this sample hardcodes one for the same reason it hardcodes
    // connection strings: it's a teaching sample, not a configurable product.
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public static bool IsExpired(DateTime expiresAtUtc, DateTime nowUtc) => nowUtc >= expiresAtUtc;

    // The evaluation request's context dictionary (role, caller, ...) is part of the cache key —
    // the same caller asking about the same resource with a different context (e.g. a different
    // role claim) is a different question and must not reuse another context's answer.
    public static string ComputeContextHash(IReadOnlyDictionary<string, string>? context)
    {
        if (context is null || context.Count == 0) return "empty";

        var normalized = string.Join(";", context.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes);
    }
}
