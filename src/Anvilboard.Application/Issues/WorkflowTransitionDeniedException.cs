namespace Anvilboard.Application.Issues;

/// <summary>
/// Signals that <see cref="IssueService.ChangeStatusAsync"/> could not apply a requested status
/// change because <c>IWorkflowService.ValidateTransitionAsync</c> denied it (e.g. no configured
/// transition rule, or a referenced workflow state no longer exists/is archived). Carries the
/// stable error code from <c>TransitionValidationResult</c> so HTTP/CLI/MCP adapters can map it to
/// a proper client error (409) instead of a generic failure.
/// </summary>
public sealed class WorkflowTransitionDeniedException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public string ErrorCode { get; } = errorCode;
}
