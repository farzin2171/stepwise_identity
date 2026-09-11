using Mini.AuthorizationService;

namespace StepwiseIdentity.Tests;

// Phase 15's one decision table: given a cached decision's expiry and the current time, is it still
// usable? Driving this through HTTP needs a running Mini.AuthorizationService and a way to fast-forward
// the clock past the TTL — driving it here needs two DateTimes, which is why it's an xunit table
// instead of a wait-30-seconds step in test-phase15.ps1.
public class DecisionCacheTests
{
    [Theory]
    // A decision cached a moment ago, checked a moment later: still fresh.
    [InlineData(0, 30, false)]
    // Right at the boundary: expiry is "now", so it's already stale (>=, not >) — a request that
    // arrives in the same instant the TTL lapses gets re-evaluated, not a stale free ride.
    [InlineData(30, 30, true)]
    // Long past its TTL.
    [InlineData(45, 30, true)]
    public void IsExpired_comparesAgainstExpiryBoundary(int secondsElapsed, int ttlSeconds, bool expectedExpired)
    {
        var createdAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var expiresAt = createdAt.AddSeconds(ttlSeconds);
        var now = createdAt.AddSeconds(secondsElapsed);

        Assert.Equal(expectedExpired, DecisionCache.IsExpired(expiresAt, now));
    }

    [Fact]
    public void ComputeContextHash_isOrderIndependent()
    {
        var a = new Dictionary<string, string> { { "role", "Admin" }, { "caller", "User" } };
        var b = new Dictionary<string, string> { { "caller", "User" }, { "role", "Admin" } };

        Assert.Equal(DecisionCache.ComputeContextHash(a), DecisionCache.ComputeContextHash(b));
    }

    [Fact]
    public void ComputeContextHash_differsWhenAValueDiffers()
    {
        var admin = new Dictionary<string, string> { { "role", "Admin" } };
        var member = new Dictionary<string, string> { { "role", "Member" } };

        Assert.NotEqual(DecisionCache.ComputeContextHash(admin), DecisionCache.ComputeContextHash(member));
    }

    [Fact]
    public void ComputeContextHash_treatsNullAndEmptyTheSame()
    {
        Assert.Equal(DecisionCache.ComputeContextHash(null), DecisionCache.ComputeContextHash(new Dictionary<string, string>()));
    }
}
