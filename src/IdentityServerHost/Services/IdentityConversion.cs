namespace IdentityServerHost.Services;

// The decision table behind /api/user/convert/{userId} — pulled out as a pure static class precisely so
// it can be tested as a table instead of through HTTP. Phase 11 is the first phase whose new logic is
// genuinely branching (which direction, and is this id even convertible), which is what the phase
// conventions say earns the xunit project: see ../../../tests/StepwiseIdentity.Tests.
//
// The id format this parses is not invented here. Controllers/ExternalController.cs has built local
// subject ids as "external:{scheme}:{externalSubjectId}" since Phase 4, and ExternalUserStore keys on
// exactly that string. Real IdG counterpart: UserStore.FindByExternalProviderAsync((ProviderName,
// ProviderSubjectId)), which stores the pair in two columns instead of packing it into one key — so the
// real system needs no parsing at all, and this parsing exists only because this sample chose a
// composite string key (see Data/UserDbContext.cs's comment on why).
public static class IdentityConversion
{
    public const string ExternalSubjectPrefix = "external";

    // Returns null when the id is not an externally-provisioned local subject at all. The local test
    // users (alice = "1", bob = "2") are the honest case: they have no external identity anywhere, so
    // "what is this user's external id" has no answer rather than a wrong one.
    public static ExternalIdentity? ParseLocalSubjectId(string? localSubjectId)
    {
        if (string.IsNullOrWhiteSpace(localSubjectId))
        {
            return null;
        }

        // Split into at most three parts, so a provider subject id that itself contains a colon
        // survives intact in the third segment. Splitting unbounded and taking [1]/[2] would silently
        // truncate those.
        var segments = localSubjectId.Split(':', 3);
        if (segments.Length != 3)
        {
            return null;
        }

        // Case-sensitive on purpose: these strings are database keys written by ExternalController, not
        // user input being normalised. Accepting "External:..." here would let a lookup succeed against
        // a key shape that nothing in this system ever writes.
        if (segments[0] != ExternalSubjectPrefix)
        {
            return null;
        }

        if (segments[1].Length == 0 || segments[2].Length == 0)
        {
            return null;
        }

        return new ExternalIdentity(segments[1], segments[2]);
    }

    // The reverse direction's key builder. Kept next to the parser so the two can't drift apart — the
    // round-trip property (parse then build gives the original) is what the test asserts.
    public static string BuildLocalSubjectId(string scheme, string externalSubjectId) =>
        $"{ExternalSubjectPrefix}:{scheme}:{externalSubjectId}";
}

// Scheme is the authentication scheme the identity was federated through ("external-idp",
// "initech-external-idp"); SubjectId is that provider's own subject id for the user.
public record ExternalIdentity(string Scheme, string SubjectId);
