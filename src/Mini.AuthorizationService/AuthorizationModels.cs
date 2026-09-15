namespace Mini.AuthorizationService;

public record AuthorizationEvaluationRequest(
    string ResourceName,
    string? Subject = null,
    string? Action = null,
    Dictionary<string, string>? Context = null);

public record AuthorizationResponse(bool Authorized, string Reason);

// Phase 21: the body for the policy-admin upsert endpoint. Name/PolicyType/Order are optional so a
// caller can send just the Condition to update an existing policy without repeating fields it isn't
// changing; a brand-new policy falls back to defaults (see Program.cs's PUT /policies/{tenantKey}/{resourceName}).
public record UpdatePolicyRequest(string Condition, string? Name = null, string? PolicyType = null, int? Order = null);
