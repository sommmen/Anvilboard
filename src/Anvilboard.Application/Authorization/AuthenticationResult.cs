namespace Anvilboard.Application.Authorization;

/// <summary>
/// The non-exceptional result of <see cref="IWorkspaceAuthorizationService.AuthenticateAsync"/>.
/// A denial never discloses whether a workspace or account exists (AC-101/AC-102): only the
/// stable <see cref="ErrorCode"/> is safe to return to the caller.
/// </summary>
public sealed record AuthenticationResult(
    bool IsAuthenticated,
    ActorContext? Actor = null,
    string? ErrorCode = null)
{
    public static AuthenticationResult Succeeded(ActorContext actor) => new(true, actor);

    public static AuthenticationResult Failed(string errorCode) => new(false, ErrorCode: errorCode);
}
