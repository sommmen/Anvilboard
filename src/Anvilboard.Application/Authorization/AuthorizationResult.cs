namespace Anvilboard.Application.Authorization;

/// <summary>
/// The non-exceptional result of <see cref="IWorkspaceAuthorizationService.AuthorizeAsync"/>.
/// </summary>
public sealed record AuthorizationResult(bool IsAuthorized, string? ErrorCode = null)
{
    public static AuthorizationResult Authorized() => new(true);

    public static AuthorizationResult Denied(string errorCode = "FORBIDDEN") => new(false, errorCode);
}
