namespace Anvilboard.Application.Workflows;

/// <summary>
/// Signals invalid workflow-configuration input using a stable §7.7 catalog error code.
/// </summary>
/// <remarks>
/// The code is an <em>instance</em> member rather than a constant because the same failure class
/// now spans more than one catalog code: a malformed key is <see cref="ValidationFailed"/> (400),
/// but a duplicate state key or transition edge is <see cref="ResourceAlreadyExists"/> (409), and a
/// replacement state that no longer exists is <see cref="ReferencedEntityNotFound"/> (404).
/// Adapters translate with <c>ErrorCodeCatalog.HttpStatusFor(ex.ErrorCode)</c> rather than a
/// hand-written switch, so the HTTP surface cannot drift from the catalog
/// (<c>docs/plans/workflow-admin-surface.md</c> §8.1, DR-WFA-007).
/// </remarks>
public sealed class WorkflowValidationException(string errorCode, string message)
    : InvalidOperationException(message)
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string ResourceAlreadyExists = "RESOURCE_ALREADY_EXISTS";
    public const string ReferencedEntityNotFound = "REFERENCED_ENTITY_NOT_FOUND";

    /// <summary>Defaults to <see cref="ValidationFailed"/>, the code every pre-existing throw site used.</summary>
    public WorkflowValidationException(string message)
        : this(ValidationFailed, message)
    {
    }

    public string ErrorCode { get; } = errorCode;
}
