namespace Anvilboard.Application.Workflows;

/// <summary>
/// The non-exceptional result of checking a requested workflow transition. Callers must persist
/// an issue state change only after receiving <see cref="IsAllowed"/>; a denial carries the stable
/// error code and contextual detail needed by HTTP, CLI, and MCP adapters.
/// </summary>
public sealed record TransitionValidationResult(
    bool IsAllowed,
    string? ErrorCode = null,
    string? Message = null)
{
    public static TransitionValidationResult Allowed() => new(true);

    public static TransitionValidationResult Denied(string errorCode, string message) =>
        new(false, errorCode, message);
}
