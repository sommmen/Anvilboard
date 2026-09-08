namespace Anvilboard.Application.Workflows;

/// <summary>Signals invalid workflow-configuration input using a stable error code.</summary>
public sealed class WorkflowValidationException(string message) : InvalidOperationException(message)
{
    public const string ErrorCode = "VALIDATION_FAILED";
}
