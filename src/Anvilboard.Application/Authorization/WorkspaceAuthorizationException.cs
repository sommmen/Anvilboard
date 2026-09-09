namespace Anvilboard.Application.Authorization;

/// <summary>Stable application error thrown by bootstrap/revocation flows that cannot proceed
/// (e.g. bootstrap attempted twice, malformed request). Routine authentication/authorization
/// denials are returned as values (see <see cref="AuthenticationResult"/>/
/// <see cref="AuthorizationResult"/>), never thrown, so callers cannot forget to check them.</summary>
public sealed class WorkspaceAuthorizationException(string errorCode, string message) : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
