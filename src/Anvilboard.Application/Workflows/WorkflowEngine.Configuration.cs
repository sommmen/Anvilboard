using System.Text.RegularExpressions;
using Anvilboard.Application.Auditing;
using Anvilboard.Domain;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Workflows;

/// <summary>
/// The workflow-configuration half of <see cref="WorkflowEngine"/>: the reads and mutations that
/// administrators drive from REST, CLI, and MCP (<c>docs/plans/workflow-admin-surface.md</c> §8).
/// </summary>
/// <remarks>
/// <para>
/// Every mutation here emits an audit event — on success <em>and</em> on rejection (DR-WFA-008) —
/// before returning. Placing the emission inside the service rather than in each adapter is what
/// makes <c>FR-WS-002 AC4</c> structural: a future adapter gets the audit trail by construction,
/// and only this layer can see derived facts such as how many issues an archive reassigned.
/// </para>
/// <para>
/// Duplicate-key and duplicate-edge collisions are checked up front <em>and</em> caught from the
/// database's unique indexes, because the pre-check is advisory under concurrency: two simultaneous
/// creates can both pass it. Translating <see cref="DbUpdateException"/> keeps the loser of that
/// race on the documented 409 instead of a 500 (§7.4).
/// </para>
/// </remarks>
public sealed partial class WorkflowEngine
{
    private const string StateTarget = "workflow_state";
    private const string TransitionTarget = "workflow_transition";

    public async Task<IReadOnlyList<WorkflowStateDto>> ListWorkflowStatesAsync(
        WorkspaceId workspaceId,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var states = await db.WorkflowStates
            .AsNoTracking()
            .Where(state => state.WorkspaceId == workspaceId && (includeArchived || !state.IsArchived))
            .OrderBy(state => state.Order)
            .ThenBy(state => state.Key)
            .ToListAsync(ct);

        return [.. states.Select(WorkflowStateDto.FromState)];
    }

    public async Task<IReadOnlyList<WorkflowTransitionDto>> ListWorkflowTransitionsAsync(
        WorkspaceId workspaceId,
        CancellationToken ct = default)
    {
        var rows = await db.WorkflowTransitions
            .AsNoTracking()
            .Where(transition => transition.WorkspaceId == workspaceId)
            .Join(
                db.WorkflowStates.AsNoTracking(),
                transition => transition.FromStateId,
                state => state.Id,
                (transition, from) => new { transition, FromKey = from.Key })
            .Join(
                db.WorkflowStates.AsNoTracking(),
                row => row.transition.ToStateId,
                state => state.Id,
                (row, to) => new { row.transition, row.FromKey, ToKey = to.Key })
            .OrderBy(row => row.FromKey)
            .ThenBy(row => row.ToKey)
            .ToListAsync(ct);

        return
        [
            .. rows.Select(row => new WorkflowTransitionDto(
                row.transition.Id.Value,
                row.transition.FromStateId.Value,
                row.FromKey,
                row.transition.ToStateId.Value,
                row.ToKey)),
        ];
    }

    public async Task<WorkflowStateDto> CreateWorkflowStateAsync(
        WorkspaceId workspaceId,
        string key,
        string displayName,
        int order,
        bool isTerminal,
        WorkflowOperationContext operation,
        CancellationToken ct = default)
    {
        key = key?.Trim() ?? string.Empty;
        displayName = displayName?.Trim() ?? string.Empty;

        if (key.Length is < 1 or > 100 || !WorkflowKeyRegex().IsMatch(key))
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, $"key:{key}", WorkflowValidationException.ValidationFailed,
                "Workflow state key is required, must be 1-100 lower-snake characters, and may contain only a-z, 0-9, and underscores.",
                ct);
        }

        if (displayName.Length is < 1 or > 200)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, $"key:{key}", WorkflowValidationException.ValidationFailed,
                "Workflow state display name is required and must be 1-200 characters.", ct);
        }

        if (await db.WorkflowStates.AnyAsync(state => state.WorkspaceId == workspaceId && state.Key == key, ct))
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, $"key:{key}", WorkflowValidationException.ResourceAlreadyExists,
                $"Workflow state key '{key}' already exists in this workspace.", ct);
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
        await SaveOrTranslateAsync(
            workspaceId, operation, StateTarget, $"key:{key}", WorkflowValidationException.ResourceAlreadyExists,
            $"Workflow state key '{key}' already exists in this workspace.", ct);

        await AuditAsync(
            workspaceId, operation, "workflow.state.created", StateTarget, state.Id.Value.ToString(),
            $"key={state.Key};order={state.Order};isTerminal={state.IsTerminal.ToString().ToLowerInvariant()}", ct);

        return WorkflowStateDto.FromState(state);
    }

    public async Task<WorkflowStateDto> UpdateWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        string? displayName,
        int? order,
        bool? isTerminal,
        WorkflowOperationContext operation,
        string? key = null,
        CancellationToken ct = default)
    {
        if (key is not null)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, stateId.Value.ToString(),
                WorkflowValidationException.ValidationFailed,
                "Workflow state key is immutable; archive the state and create a replacement instead.", ct);
        }

        var state = await db.WorkflowStates.SingleOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.Id == stateId, ct);

        if (state is null)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, stateId.Value.ToString(),
                WorkflowValidationException.ReferencedEntityNotFound,
                $"Workflow state '{stateId}' was not found.", ct);
        }

        var changes = new List<string>();

        if (displayName is not null)
        {
            var trimmed = displayName.Trim();
            if (trimmed.Length is < 1 or > 200)
            {
                throw await RejectAsync(
                    workspaceId, operation, StateTarget, state.Id.Value.ToString(),
                    WorkflowValidationException.ValidationFailed,
                    "Workflow state display name is required and must be 1-200 characters.", ct);
            }

            if (!string.Equals(trimmed, state.DisplayName, StringComparison.Ordinal))
            {
                state.DisplayName = trimmed;
                changes.Add($"displayName={trimmed}");
            }
        }

        if (order is { } newOrder && newOrder != state.Order)
        {
            state.Order = newOrder;
            changes.Add($"order={newOrder}");
        }

        if (isTerminal is { } newIsTerminal && newIsTerminal != state.IsTerminal)
        {
            state.IsTerminal = newIsTerminal;
            changes.Add($"isTerminal={newIsTerminal.ToString().ToLowerInvariant()}");
        }

        // A request that asks for the values the state already holds is a no-op, not an event: an
        // audit log full of "changed nothing" entries is harder to read, not more complete (§7.4).
        if (changes.Count == 0)
        {
            return WorkflowStateDto.FromState(state);
        }

        await db.SaveChangesAsync(ct);

        await AuditAsync(
            workspaceId, operation, "workflow.state.updated", StateTarget, state.Id.Value.ToString(),
            $"key={state.Key};{string.Join(';', changes)}", ct);

        return WorkflowStateDto.FromState(state);
    }

    public async Task ArchiveWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        WorkflowStateId? replacementStateId,
        WorkflowOperationContext operation,
        CancellationToken ct = default)
    {
        var state = await db.WorkflowStates.SingleOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.Id == stateId, ct);

        if (state is null)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, stateId.Value.ToString(),
                WorkflowValidationException.ReferencedEntityNotFound,
                $"Workflow state '{stateId}' was not found.", ct);
        }

        // Archiving an already-archived state is the requested end state, so it succeeds silently
        // and emits nothing: a retried DELETE should not double up the audit trail.
        if (state.IsArchived)
        {
            return;
        }

        // Without at least one active state left, IssueService.GetInitialWorkflowStateIdAsync has
        // nothing to assign and issue creation breaks workspace-wide (AC-WFA-007).
        var remainingActive = await db.WorkflowStates.CountAsync(
            candidate => candidate.WorkspaceId == workspaceId && !candidate.IsArchived && candidate.Id != stateId,
            ct);
        if (remainingActive == 0)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, state.Id.Value.ToString(),
                WorkflowValidationException.ValidationFailed,
                $"Cannot archive workflow state '{state.Key}' because it is the workspace's last active state.", ct);
        }

        var dependentIssues = await db.Issues.Where(issue => issue.WorkflowStateId == stateId).ToListAsync(ct);
        if (dependentIssues.Count > 0 && replacementStateId is null)
        {
            throw await RejectAsync(
                workspaceId, operation, StateTarget, state.Id.Value.ToString(),
                WorkflowValidationException.ValidationFailed,
                $"Cannot archive workflow state '{state.Key}' because {dependentIssues.Count} issue(s) reference it and no replacement state was provided.",
                ct);
        }

        var replacementKey = "none";

        if (replacementStateId is { } replacementId)
        {
            if (replacementId == stateId)
            {
                throw await RejectAsync(
                    workspaceId, operation, StateTarget, state.Id.Value.ToString(),
                    WorkflowValidationException.ValidationFailed, "A workflow state cannot replace itself.", ct);
            }

            var replacement = await db.WorkflowStates.SingleOrDefaultAsync(
                candidate => candidate.WorkspaceId == workspaceId && candidate.Id == replacementId, ct);
            if (replacement is null || replacement.IsArchived)
            {
                throw await RejectAsync(
                    workspaceId, operation, StateTarget, state.Id.Value.ToString(),
                    WorkflowValidationException.ValidationFailed,
                    "Replacement workflow state must exist and be active in the same workspace.", ct);
            }

            replacementKey = replacement.Key;

            foreach (var issue in dependentIssues)
            {
                issue.WorkflowStateId = replacementId;
            }
        }

        state.IsArchived = true;
        await db.SaveChangesAsync(ct);

        await AuditAsync(
            workspaceId, operation, "workflow.state.archived", StateTarget, state.Id.Value.ToString(),
            $"key={state.Key};replacementKey={replacementKey};reassignedIssues={dependentIssues.Count}", ct);
    }

    public async Task<WorkflowTransitionDto> CreateWorkflowTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowStateId fromStateId,
        WorkflowStateId toStateId,
        WorkflowOperationContext operation,
        CancellationToken ct = default)
    {
        var pair = $"pair:{fromStateId.Value}->{toStateId.Value}";

        // A self-loop is never needed: IssueService already short-circuits same-state changes before
        // consulting the adjacency list, so the edge would be permanently dead configuration.
        if (fromStateId == toStateId)
        {
            throw await RejectAsync(
                workspaceId, operation, TransitionTarget, pair, WorkflowValidationException.ValidationFailed,
                "A workflow transition must connect two different states.", ct);
        }

        var endpoints = await db.WorkflowStates
            .Where(state => state.WorkspaceId == workspaceId &&
                (state.Id == fromStateId || state.Id == toStateId))
            .ToListAsync(ct);

        var from = endpoints.SingleOrDefault(state => state.Id == fromStateId);
        var to = endpoints.SingleOrDefault(state => state.Id == toStateId);

        if (from is null || to is null)
        {
            throw await RejectAsync(
                workspaceId, operation, TransitionTarget, pair,
                WorkflowValidationException.ReferencedEntityNotFound,
                "Both workflow transition endpoints must exist in this workspace.", ct);
        }

        if (from.IsArchived || to.IsArchived)
        {
            throw await RejectAsync(
                workspaceId, operation, TransitionTarget, pair, WorkflowValidationException.ValidationFailed,
                "A workflow transition cannot reference an archived state.", ct);
        }

        var duplicateMessage = $"A workflow transition from '{from.Key}' to '{to.Key}' already exists.";

        if (await db.WorkflowTransitions.AnyAsync(
                transition => transition.WorkspaceId == workspaceId &&
                    transition.FromStateId == fromStateId && transition.ToStateId == toStateId,
                ct))
        {
            throw await RejectAsync(
                workspaceId, operation, TransitionTarget, pair,
                WorkflowValidationException.ResourceAlreadyExists, duplicateMessage, ct);
        }

        var transition = new WorkflowTransition
        {
            Id = WorkflowTransitionId.New(),
            WorkspaceId = workspaceId,
            FromStateId = fromStateId,
            ToStateId = toStateId,
        };

        db.WorkflowTransitions.Add(transition);
        await SaveOrTranslateAsync(
            workspaceId, operation, TransitionTarget, pair,
            WorkflowValidationException.ResourceAlreadyExists, duplicateMessage, ct);

        await AuditAsync(
            workspaceId, operation, "workflow.transition.created", TransitionTarget,
            transition.Id.Value.ToString(), $"from={from.Key};to={to.Key}", ct);

        return new WorkflowTransitionDto(
            transition.Id.Value, from.Id.Value, from.Key, to.Id.Value, to.Key);
    }

    public async Task RemoveWorkflowTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowTransitionId transitionId,
        WorkflowOperationContext operation,
        CancellationToken ct = default)
    {
        var transition = await db.WorkflowTransitions.SingleOrDefaultAsync(
            candidate => candidate.WorkspaceId == workspaceId && candidate.Id == transitionId, ct);

        if (transition is null)
        {
            throw await RejectAsync(
                workspaceId, operation, TransitionTarget, transitionId.Value.ToString(),
                WorkflowValidationException.ReferencedEntityNotFound,
                $"Workflow transition '{transitionId}' was not found.", ct);
        }

        var keys = await db.WorkflowStates
            .AsNoTracking()
            .Where(state => state.Id == transition.FromStateId || state.Id == transition.ToStateId)
            .ToDictionaryAsync(state => state.Id, state => state.Key, ct);

        db.WorkflowTransitions.Remove(transition);
        await db.SaveChangesAsync(ct);

        await AuditAsync(
            workspaceId, operation, "workflow.transition.removed", TransitionTarget,
            transitionId.Value.ToString(),
            $"from={keys.GetValueOrDefault(transition.FromStateId, "unknown")};to={keys.GetValueOrDefault(transition.ToStateId, "unknown")}",
            ct);
    }

    /// <summary>
    /// Persists pending changes, converting the unique-index violation that a concurrent duplicate
    /// create loses into the documented catalog error instead of an unhandled 500.
    /// </summary>
    private async Task SaveOrTranslateAsync(
        WorkspaceId workspaceId,
        WorkflowOperationContext operation,
        string targetType,
        string targetId,
        string errorCode,
        string message,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            throw await RejectAsync(workspaceId, operation, targetType, targetId, errorCode, message, ct);
        }
    }

    /// <summary>
    /// Records the rejection and returns the exception to throw, so every guard clause reads as a
    /// single <c>throw await RejectAsync(...)</c> and no path can reject without auditing.
    /// </summary>
    private async Task<WorkflowValidationException> RejectAsync(
        WorkspaceId workspaceId,
        WorkflowOperationContext operation,
        string targetType,
        string targetId,
        string errorCode,
        string message,
        CancellationToken ct)
    {
        var action = targetType == StateTarget ? "workflow.state.rejected" : "workflow.transition.rejected";
        await AuditAsync(workspaceId, operation, action, targetType, targetId, $"errorCode={errorCode};{message}", ct);
        return new WorkflowValidationException(errorCode, message);
    }

    private Task AuditAsync(
        WorkspaceId workspaceId,
        WorkflowOperationContext operation,
        string action,
        string targetType,
        string targetId,
        string resultSummary,
        CancellationToken ct) =>
        audit.RecordAsync(
            new AuditEventRequest(
                workspaceId,
                operation.ActorId,
                operation.Channel,
                action,
                targetType,
                targetId,
                operation.CorrelationId,
                resultSummary),
            ct);

    [GeneratedRegex("^[a-z0-9_]+$")]
    private static partial Regex WorkflowKeyRegex();
}
