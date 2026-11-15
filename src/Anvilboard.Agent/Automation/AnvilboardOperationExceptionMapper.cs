using Anvilboard.Application.Activity;
using Anvilboard.Application.Artifacts;
using Anvilboard.Application.Auditing;
using Anvilboard.Application.Authorization;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Backup;
using Anvilboard.Application.Integrations;
using Anvilboard.Application.Issues;
using Anvilboard.Application.Workflows;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent.Automation;

/// <summary>
/// Keeps the stable error-catalog code in the message for the application exceptions that carry one.
/// </summary>
/// <remarks>
/// The default mapper redacts any exception that is not an <see cref="OperationException"/> on the
/// MCP surface. These application exceptions are written for callers and carry their catalog code in
/// the message, so agents must receive them verbatim to branch on the code; anything else stays redacted.
/// </remarks>
public sealed class AnvilboardOperationExceptionMapper(IOperationExceptionMapper? inner = null) : IOperationExceptionMapper
{
    private readonly IOperationExceptionMapper _inner = inner ?? new DefaultOperationExceptionMapper();

    public OperationInvocationResult Map(OperationFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsCallerSafe(context.Exception)
            ? OperationInvocationResult.VerbatimFailure(context.Exception.Message)
            : _inner.Map(context);
    }

    private static bool IsCallerSafe(Exception exception) => exception is
        AgentRequestException
        or ActivityQueryException
        or ArtifactException
        or AuditQueryException
        or BackupOperationException
        or BoardQueryException
        or ConcurrencyConflictException
        or IdempotencyKeyReusedException
        or IntegrationException
        or IssueLinkException
        or WorkflowTransitionDeniedException
        or WorkflowValidationException
        or WorkspaceAuthorizationException;
}
