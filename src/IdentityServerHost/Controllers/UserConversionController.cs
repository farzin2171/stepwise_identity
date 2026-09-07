using Duende.IdentityServer;
using IdentityServerHost.Data;
using IdentityServerHost.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IdentityServerHost.Controllers;

// Real IdG counterpart: the endpoint Services.User calls as IIdentityClientV1 —
// GET /user/convert/{userId}?convertTo={conversionType}. Phase 11 adds it because this is the arrow
// that makes the IdG <-> User relationship bidirectional: the IdG calls Mini.UserService for the role
// claim during token issuance, and Mini.UserService calls back here to convert ids, because the
// local <-> external mapping lives in the IdG's UserDbContext and nowhere else.
//
// Protected by Duende's LOCAL API authentication, not a JwtBearer handler. AddLocalApiAuthentication()
// validates the token in-process through IdentityServer's own ITokenValidator, so this host does not
// have to fetch its own discovery document over HTTP from itself to validate a token it minted itself.
// Pointing a JwtBearer handler at Authority = "https://localhost:5001" from inside :5001 works, but it
// is a self-referential network call for a question the process can already answer locally.
//
// The token must carry the "IdentityServerApi" scope (IdentityServerConstants.LocalApi.ScopeName) —
// see Configurations/IdentityServerConfig.json for the apiScope and the three "userservice-svc.{tenant}"
// clients that are allowed to request it. A user's own access token cannot reach this endpoint: it has
// no such scope.
[ApiController]
[Route("api/user")]
[Authorize(IdentityServerConstants.LocalApi.PolicyName)]
public class UserConversionController(UserDbContext db) : ControllerBase
{
    [HttpGet("convert/{userId}")]
    public async Task<IActionResult> Convert(string userId, [FromQuery] string convertTo, CancellationToken ct)
    {
        return convertTo?.ToLowerInvariant() switch
        {
            "external" => await ToExternalAsync(userId, ct),
            "local" => await ToLocalAsync(userId, ct),
            _ => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Unsupported convertTo",
                detail: $"'{convertTo}' is not a conversion target — expected 'Local' or 'External'.")
        };
    }

    // Local -> external. Two things have to be true, and they fail differently: the id has to LOOK like
    // a federated local subject (a local password user never will), and the mapping has to actually
    // exist in the store. Both answer 404 to the caller, but only the second one means "this user was
    // never provisioned here."
    private async Task<IActionResult> ToExternalAsync(string localSubjectId, CancellationToken ct)
    {
        var external = IdentityConversion.ParseLocalSubjectId(localSubjectId);
        if (external is null)
        {
            return NotFound();
        }

        var exists = await db.Users.AnyAsync(u => u.SubjectId == localSubjectId, ct);
        return exists
            ? Ok(new { userId = localSubjectId, convertedUserId = external.SubjectId, provider = external.Scheme })
            : NotFound();
    }

    // External -> local. This direction is where packing (scheme, subjectId) into one string key costs
    // something: the caller supplies only the provider's subject id, so the scheme is unknown and the
    // lookup has to be a suffix match. Two providers that happen to issue the same subject id for
    // different people produce two rows, and this endpoint genuinely cannot tell which one was meant —
    // so it says so with a 409 rather than picking one.
    //
    // The real IdG has no such problem: its User table stores ProviderName and ProviderSubjectId in
    // separate columns, so the same lookup is an exact match on one of them. This is the concrete price
    // of the composite-key shortcut Data/UserDbContext.cs took in Phase 5, and it only became visible
    // once something needed to search in this direction.
    private async Task<IActionResult> ToLocalAsync(string externalSubjectId, CancellationToken ct)
    {
        var suffix = $":{externalSubjectId}";
        var matches = await db.Users
            .Where(u => u.SubjectId.EndsWith(suffix))
            .Select(u => u.SubjectId)
            .Take(2)
            .ToListAsync(ct);

        return matches.Count switch
        {
            0 => NotFound(),
            1 => Ok(new { userId = externalSubjectId, convertedUserId = matches[0] }),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Ambiguous external subject id",
                detail: $"More than one provisioned identity ends with '{suffix}' — the provider scheme is " +
                        "needed to disambiguate, and this API's shape doesn't carry one.")
        };
    }
}
