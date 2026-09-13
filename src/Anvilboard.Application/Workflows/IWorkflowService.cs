using Anvilboard.Domain;

namespace Anvilboard.Application.Workflows;

/// <summary>Owns workspace workflow configuration and validates proposed state changes.</summary>
/// <remarks>
/// Every mutation takes a <see cref="WorkflowOperationContext"/> so the audit event is emitted here
/// rather than by each adapter (<c>docs/plans/workflow-admin-surface.md</c> DR-WFA-001). Mutations
/// return DTOs rather than tracked entities so an adapter cannot write around this validation.
/// </remarks>
public interface IWorkflowService
{
    Task<TransitionValidationResult> ValidateTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowStateId currentStateId,
        WorkflowStateId targetStateId,
        CancellationToken ct = default);

    /// <summary>Lists a workspace's states in board order; archived states are excluded by default.</summary>
    Task<IReadOnlyList<WorkflowStateDto>> ListWorkflowStatesAsync(
        WorkspaceId workspaceId,
        bool includeArchived = false,
        CancellationToken ct = default);

    /// <summary>Lists the workspace's allowed transition edges, including edges touching archived states.</summary>
    Task<IReadOnlyList<WorkflowTransitionDto>> ListWorkflowTransitionsAsync(
        WorkspaceId workspaceId,
        CancellationToken ct = default);

    Task<WorkflowStateDto> CreateWorkflowStateAsync(
        WorkspaceId workspaceId,
        string key,
        string displayName,
        int order,
        bool isTerminal,
        WorkflowOperationContext operation,
        CancellationToken ct = default);

    /// <summary>
    /// Applies the supplied non-null fields to a state. <paramref name="key"/> is accepted only to
    /// reject attempted renames in the service, where the rejection audit is owned (DR-WFA-003).
    /// </summary>
    Task<WorkflowStateDto> UpdateWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        string? displayName,
        int? order,
        bool? isTerminal,
        WorkflowOperationContext operation,
        string? key = null,
        CancellationToken ct = default);

    Task ArchiveWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        WorkflowStateId? replacementStateId,
        WorkflowOperationContext operation,
        CancellationToken ct = default);

    Task<WorkflowTransitionDto> CreateWorkflowTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowStateId fromStateId,
        WorkflowStateId toStateId,
        WorkflowOperationContext operation,
        CancellationToken ct = default);

    Task RemoveWorkflowTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowTransitionId transitionId,
        WorkflowOperationContext operation,
        CancellationToken ct = default);
}
