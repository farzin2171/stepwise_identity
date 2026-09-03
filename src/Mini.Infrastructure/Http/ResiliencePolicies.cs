using Polly;
using Polly.Extensions.Http;

namespace Mini.Infrastructure.Http;

// Extracted in Phase 10, and this one needs no justification beyond reading the code it replaced.
// IdentityServerHost/Program.cs and MvcClient/Program.cs each declared their own identical
// RetryPolicy()/CircuitBreakerPolicy() static local functions, and IdentityServerHost's carried a
// comment admitting it: "Same Polly retry + circuit-breaker shape MvcClient already established for its
// own external calls (Program.cs there) — reused verbatim rather than reinvented."
//
// "Reused verbatim" was generous. It was copied. Two copies of a resilience policy is the textbook
// version of the drift problem: the next person to decide three retries is better than two, or that
// 30 seconds is too long to keep a circuit open, changes one file and leaves the other alone — and
// nothing fails, so nobody finds out until the two services behave differently under load.
//
// Apply counterpart: the real Apply configures these through DIT.HTTP's config-driven policies
// (retry counts and breaker thresholds come from appsettings, per named client) rather than hardcoded
// method bodies. This sample keeps the values in code — extracting them was this phase's job; making
// them configurable is a different phase's, and EqusoftInfra Series 9 teaches the library that does it.
public static class ResiliencePolicies
{
    // Two attempts after the first failure, backing off 2s then 4s. HandleTransientHttpError covers
    // 5xx, 408, and HttpRequestException — deliberately NOT 4xx, because retrying a 401 or a 404 just
    // means failing more slowly.
    public static IAsyncPolicy<HttpResponseMessage> Retry() =>
        HttpPolicyExtensions.HandleTransientHttpError()
            .WaitAndRetryAsync(2, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

    // Three consecutive failures opens the circuit for 30 seconds: every call in that window fails
    // immediately with BrokenCircuitException instead of waiting for a timeout.
    //
    // Worth knowing, because Phase 9 hit it for real: the breaker makes a dead dependency look like a
    // *different* failure than it is. Stopping ExternalServicesStub and logging in produced
    // "The circuit is now open and is not allowing calls" rather than "connection refused," and the
    // stub had to be running for 30 seconds before the next attempt would even try. If a dependency
    // you just fixed still looks broken, check whether you are inside this window.
    public static IAsyncPolicy<HttpResponseMessage> CircuitBreaker() =>
        HttpPolicyExtensions.HandleTransientHttpError()
            .CircuitBreakerAsync(3, TimeSpan.FromSeconds(30));
}
