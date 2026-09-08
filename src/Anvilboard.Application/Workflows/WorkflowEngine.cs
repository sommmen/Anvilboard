using System.Text.RegularExpressions;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Workflows;

/// <summary>
/// Persists a workspace's workflow configuration and validates state changes against its explicit
/// transition edges. It deliberately does not mutate issues: the Issue &amp; Board Service owns that
/// mutation, activity recording, hooks, and concurrency/version handling.
/// </summary>
public sealed partial class WorkflowEngine(AnvilboardDbContext db) : IWorkflowService
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

    public async Task<WorkflowState> CreateWorkflowStateAsync(
        WorkspaceId workspaceId,
        string key,
        string displayName,
        int order,
        bool isTerminal,
        CancellationToken ct = default)
    {
        key = key?.Trim() ?? string.Empty;
        displayName = displayName?.Trim() ?? string.Empty;

        if (key.Length is < 1 or > 100 || !WorkflowKeyRegex().IsMatch(key))
        {
            throw new WorkflowValidationException(
                "Workflow state key is required, must be 1-100 lower-snake characters, and may contain only a-z, 0-9, and underscores.");
        }

        if (displayName.Length is < 1 or > 200)
        {
            throw new WorkflowValidationException("Workflow state display name is required and must be 1-200 characters.");
        }

        if (await db.WorkflowStates.AnyAsync(
                state => state.WorkspaceId == workspaceId && state.Key == key, ct))
        {
            throw new WorkflowValidationException($"Workflow state key '{key}' already exists in this workspace.");
        }

        var state = new WorkflowState
        {
            Id = WorkflowStateId.New(),
            WorkspaceId = workspaceId,
            Key = key,
            DisplayName = displayName,
            Order = order,
            IsTerminal = isTerminal,
            IsArchived = false,
        };

        db.WorkflowStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    public async Task ArchiveWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        WorkflowStateId? replacementStateId,
        CancellationToken ct = default)
    {
        var state = await db.WorkflowStates.SingleOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.Id == stateId, ct)
            ?? throw new WorkflowValidationException($"Workflow state '{stateId}' was not found.");

        if (state.IsArchived)
        {
            return;
        }

        var dependentIssues = await db.Issues.Where(issue => issue.WorkflowStateId == stateId).ToListAsync(ct);
        if (dependentIssues.Count > 0 && replacementStateId is null)
        {
            throw new WorkflowValidationException(
                $"Cannot archive workflow state '{state.Key}' because {dependentIssues.Count} issue(s) reference it and no replacement state was provided.");
        }

        if (replacementStateId is { } replacementId)
        {
            if (replacementId == stateId)
            {
                throw new WorkflowValidationException("A workflow state cannot replace itself.");
            }

            var replacement = await db.WorkflowStates.SingleOrDefaultAsync(
                candidate => candidate.WorkspaceId == workspaceId && candidate.Id == replacementId, ct);
            if (replacement is null || replacement.IsArchived)
            {
                throw new WorkflowValidationException("Replacement workflow state must exist and be active in the same workspace.");
            }

            foreach (var issue in dependentIssues)
            {
                issue.WorkflowStateId = replacementId;
            }
        }

        state.IsArchived = true;
        await db.SaveChangesAsync(ct);
    }

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex WorkflowKeyRegex();
}
