using Anvilboard.Application.Authorization;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Workflows;

/// <summary>
/// Persists a workspace's workflow configuration and validates state changes against its explicit
/// transition edges. It deliberately does not mutate issues: the Issue &amp; Board Service owns that
/// mutation, activity recording, hooks, and concurrency/version handling.
/// </summary>
/// <remarks>
/// The configuration mutations live in <c>WorkflowEngine.Configuration.cs</c>, which also owns the
/// audit emission for every one of them.
/// </remarks>
public sealed partial class WorkflowEngine(AnvilboardDbContext db, IAuditService audit) : IWorkflowService
{
    public async Task<TransitionValidationResult> ValidateTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowStateId currentStateId,
        WorkflowStateId targetStateId,
        CancellationToken ct = default)
    {
        var states = await db.WorkflowStates
            .Where(state => state.WorkspaceId == workspaceId &&
                (state.Id == currentStateId || state.Id == targetStateId))
            .ToListAsync(ct);

        var currentState = states.SingleOrDefault(state => state.Id == currentStateId);
        if (currentState is null)
        {
            return TransitionValidationResult.Denied(
                "REFERENCED_ENTITY_NOT_FOUND", $"Workflow state '{currentStateId}' was not found.");
        }

        var targetState = states.SingleOrDefault(state => state.Id == targetStateId);
        if (targetState is null)
        {
            return TransitionValidationResult.Denied(
                "REFERENCED_ENTITY_NOT_FOUND", $"Workflow state '{targetStateId}' was not found.");
        }

        if (currentState.IsArchived)
        {
            return TransitionValidationResult.Denied(
                "INVALID_WORKFLOW_TRANSITION", $"Workflow state '{currentStateId}' is archived.");
        }

        if (targetState.IsArchived)
        {
            return TransitionValidationResult.Denied(
                "INVALID_WORKFLOW_TRANSITION", $"Workflow state '{targetStateId}' is archived.");
        }

        if (currentStateId == targetStateId)
        {
            return TransitionValidationResult.Allowed();
        }

        var configured = await db.WorkflowTransitions.AnyAsync(
            transition => transition.WorkspaceId == workspaceId &&
                transition.FromStateId == currentStateId && transition.ToStateId == targetStateId,
            ct);

        return configured
            ? TransitionValidationResult.Allowed()
            : TransitionValidationResult.Denied(
                "INVALID_WORKFLOW_TRANSITION",
                $"No configured transition rule from '{currentState.Key}' to '{targetState.Key}'.");
    }
}
