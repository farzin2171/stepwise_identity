using IdentityServerHost.Services;

namespace StepwiseIdentity.Tests;

// Phase 11 is the first phase whose new logic branches on its input, which is what the phase
// conventions say earns this project: "Decision tables are documented far better by a table-driven
// test than by a black-box HTTP script."
//
// The decision this table documents: given a subject id, does this system have an external identity
// for it, and what is it? Driving that through HTTP would need a provisioned federated login per row,
// a running IdentityServerHost and a database — for a function whose entire job is parsing a string.
public class IdentityConversionTests
{
    // Every row here is a shape that actually occurs in this repo, not an invented edge case:
    //   external-idp / initech-external-idp / entra-acme  are the three schemes in
    //   appsettings.Development.json and IdentityServerConfig.json, and "1"/"2" are TestUsers' alice
    //   and bob. The colon-bearing row matters because Entra's own subject ids are opaque base64url
    //   strings — see the third row's real value, taken from this repo's own MiniIdG Users table.
    [Theory]
    [InlineData("external:external-idp:ext-1", "external-idp", "ext-1")]
    [InlineData("external:initech-external-idp:ext-1", "initech-external-idp", "ext-1")]
    [InlineData("external:entra-acme:Kzl94NcQXmzn2ygrX3WsplM6AEaTexYEzxjaZ3loZPM", "entra-acme", "Kzl94NcQXmzn2ygrX3WsplM6AEaTexYEzxjaZ3loZPM")]
    // A provider subject id containing a colon survives intact, because the split is bounded to three
    // parts. An unbounded Split(':') would return four segments here and silently truncate to "with".
    [InlineData("external:some-scheme:with:colons", "some-scheme", "with:colons")]
    public void ParsesAFederatedLocalSubjectId(string localSubjectId, string expectedScheme, string expectedSubject)
    {
        var parsed = IdentityConversion.ParseLocalSubjectId(localSubjectId);

        Assert.NotNull(parsed);
        Assert.Equal(expectedScheme, parsed.Scheme);
        Assert.Equal(expectedSubject, parsed.SubjectId);
    }

    // Null is the answer, not an exception: "this user has no external identity" is a normal, expected
    // outcome that UserConversionController turns into a 404. The local password users are the whole
    // reason — alice exists, and has no external id anywhere.
    [Theory]
    // TestUsers' own subject ids. The single most common non-convertible input in this repo.
    [InlineData("1")]
    [InlineData("2")]
    // Not the "external" prefix at all.
    [InlineData("something:else:entirely")]
    // Too few segments to name both a scheme and a subject.
    [InlineData("external:only-two")]
    [InlineData("external")]
    // Case-sensitive: ExternalController writes the prefix lowercase and ExternalUserStore keys on the
    // exact string, so accepting a different casing here would claim a mapping exists under a key
    // nothing ever writes.
    [InlineData("External:external-idp:ext-1")]
    [InlineData("EXTERNAL:external-idp:ext-1")]
    // Empty segments name nothing. "external::ext-1" has no scheme; "external:scheme:" has no subject.
    [InlineData("external::ext-1")]
    [InlineData("external:external-idp:")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReturnsNullForAnythingThatIsNotAFederatedLocalSubjectId(string? localSubjectId)
    {
        Assert.Null(IdentityConversion.ParseLocalSubjectId(localSubjectId));
    }

    // The property that keeps the two directions honest. UserConversionController's External branch
    // parses, and its Local branch matches against a key built the same way ExternalController builds
    // it — if these two ever disagree about the separator or the prefix, every federated login's
    // conversion breaks in one direction only, which is the kind of asymmetry a round-trip assertion
    // catches and two separate example-based tests do not.
    [Theory]
    [InlineData("external-idp", "ext-1")]
    [InlineData("initech-external-idp", "ext-1")]
    [InlineData("some-scheme", "with:colons")]
    public void BuildAndParseRoundTrip(string scheme, string externalSubjectId)
    {
        var built = IdentityConversion.BuildLocalSubjectId(scheme, externalSubjectId);
        var parsed = IdentityConversion.ParseLocalSubjectId(built);

        Assert.NotNull(parsed);
        Assert.Equal(scheme, parsed.Scheme);
        Assert.Equal(externalSubjectId, parsed.SubjectId);
    }
}
