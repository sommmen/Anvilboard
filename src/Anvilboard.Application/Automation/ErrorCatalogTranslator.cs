using Anvilboard.Application.Authorization;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Workflows;

namespace Anvilboard.Application.Automation;

/// <summary>
/// Single translation point from an <c>Anvilboard.Application</c> exception to the stable §7.7
/// catalog codes, invoked identically by the REST minimal-API endpoint filters, the CLI
/// <c>OperationInvoker</c>, and the MCP <c>McpOperationAdapter</c>, so no channel can diverge from
/// the §7.6/§7.7 catalog (see docs/features/agent-and-automation-surface.md,
/// "Error Catalog Translation"). Unlike the spec's illustrative sample — which pattern-matches on
/// a dedicated exception subtype per catalog code — this codebase already carries the stable code
/// on a small set of generic exceptions via their <c>ErrorCode</c> property/constant, so this
/// translator matches on those existing types instead of introducing a parallel exception
/// hierarchy.
/// </summary>
public static class ErrorCatalogTranslator
{
    public static ProblemDetailsResult Translate(Exception ex, string correlationId) => ex switch
    {
        WorkspaceAuthorizationException e => Build(e.ErrorCode, correlationId, e.Message),
        IssueLinkException e => Build(e.ErrorCode, correlationId, e.Message),
        WorkflowTransitionDeniedException e => Build(e.ErrorCode, correlationId, e.Message),
        WorkflowValidationException e => Build(WorkflowValidationException.ErrorCode, correlationId, e.Message),
        IdempotencyKeyReusedException e => Build(IdempotencyKeyReusedException.ErrorCode, correlationId, e.Message),

        // Never a documented contract value (§7.7); logged only, not part of the public catalog.
        _ => Build(ErrorCodeCatalog.InternalError, correlationId, detail: null),
    };

    /// <summary>
    /// Translates a stable catalog code directly, for callers holding a result value
    /// (<c>AuthenticationResult</c>, <c>AuthorizationResult</c>, <c>TransitionValidationResult</c>)
    /// rather than a thrown exception.
    /// </summary>
    public static ProblemDetailsResult Translate(string errorCode, string correlationId, string? detail = null) =>
        Build(errorCode, correlationId, detail);

    private static ProblemDetailsResult Build(string errorCode, string correlationId, string? detail) =>
        new(ErrorCodeCatalog.HttpStatusFor(errorCode), errorCode, correlationId, detail);
}
