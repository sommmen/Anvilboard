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
/// Surfaces the stable error-catalog code in the message for the application exceptions that carry one.
/// </summary>
/// <remarks>
/// The default mapper redacts any exception that is not an <see cref="OperationException"/> on the
/// MCP surface. These application exceptions are written for callers, so agents receive their message,
/// prefixed with the catalog code from <c>ErrorCode</c> when the message does not already start with it,
/// to branch on the code. Anything else, including a backup artifact-store failure whose message embeds a
/// server-side path, stays redacted.
/// </remarks>
public sealed class AnvilboardOperationExceptionMapper(IOperationExceptionMapper? inner = null) : IOperationExceptionMapper
{
    private readonly IOperationExceptionMapper _inner = inner ?? new DefaultOperationExceptionMapper();

    public OperationInvocationResult Map(OperationFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var exception = context.Exception;
        return IsCallerSafe(exception)
            ? OperationInvocationResult.VerbatimFailure(WithCode(exception))
            : _inner.Map(context);
    }

    private static string WithCode(Exception exception)
    {
        var code = ErrorCodeOf(exception);
        return code is null || exception.Message.StartsWith(code, StringComparison.Ordinal)
            ? exception.Message
            : $"{code}: {exception.Message}";
    }

    private static string? ErrorCodeOf(Exception exception) => exception switch
    {
        ActivityQueryException e => e.ErrorCode,
        ArtifactException e => e.ErrorCode,
        AuditQueryException e => e.ErrorCode,
        BackupOperationException e => e.ErrorCode,
        BoardQueryException e => e.ErrorCode,
        ConcurrencyConflictException e => e.ErrorCode,
        IdempotencyKeyReusedException => IdempotencyKeyReusedException.ErrorCode,
        IntegrationException e => e.ErrorCode,
        IssueLinkException e => e.ErrorCode,
        WorkflowTransitionDeniedException e => e.ErrorCode,
        WorkflowValidationException e => e.ErrorCode,
        WorkspaceAuthorizationException e => e.ErrorCode,
        _ => null,
    };

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

