namespace Mini.Infrastructure.Identity;

// Services.Authorization's real IIdentityContext models four kinds of caller (User, Service,
// Guest, OnBehalfOf — see Libraries.Infrastructure/DIT.Identity/IdentityType.cs). This sample only
// ever sees two: a human who logged into MvcClient/ReactSpa, or one of IdentityServerHost's
// client-credentials clients acting with no user behind it at all.
public enum IdentityType
{
    User,
    Service
}
