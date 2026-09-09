namespace Mini.AuthorizationService;

public record AuthorizationEvaluationRequest(
    string ResourceName,
    string? Subject = null,
    string? Action = null,
    Dictionary<string, string>? Context = null);

public record AuthorizationResponse(bool Authorized, string Reason);
