namespace Mini.UserService.Connectors;

// Real counterpart: DIT.Connectors' ServiceExtensibilityResult<T> — Success / Value / Error /
// Exception. Every connector call comes back as one of these instead of throwing.
//
// The reason, from the library map's own design table: "A tenant's external API failing is expected
// weather, not an exceptional condition — callers must handle it every time, and a result object
// forces that." That is not a style preference here. The cascading chain in
// Handlers/ActionHandlerBase.cs has to be able to look at a failure and decide whether to continue,
// and an exception thrown out of a connector would unwind past the loop that makes that decision.
//
// Note the three-way distinction, which a bare T? cannot express:
//   Success + Value      the connector answered, and here is the answer
//   Success + no Value   the connector answered "no such user" — a 404 from the tenant's own API is
//                        an ANSWER, not a fault, and must not stop a cascade or fail a request
//   not Success          the connector could not answer at all (unreachable, refused, 5xx)
public class ServiceExtensibilityResult<T>
{
    public bool Success { get; private init; }
    public T? Value { get; private init; }
    public string? Error { get; private init; }
    public Exception? Exception { get; private init; }

    public static ServiceExtensibilityResult<T> Succeeded(T? value) =>
        new() { Success = true, Value = value };

    public static ServiceExtensibilityResult<T> Failed(string error, Exception? exception = null) =>
        new() { Success = false, Error = error, Exception = exception };
}
