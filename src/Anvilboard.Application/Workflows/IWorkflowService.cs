using Anvilboard.Domain;

namespace Anvilboard.Application.Workflows;

/// <summary>Owns workspace workflow configuration and validates proposed state changes.</summary>
public interface IWorkflowService
{
    Task<TransitionValidationResult> ValidateTransitionAsync(
        WorkspaceId workspaceId,
        WorkflowStateId currentStateId,
        WorkflowStateId targetStateId,
        CancellationToken ct = default);

    Task<WorkflowState> CreateWorkflowStateAsync(
        WorkspaceId workspaceId,
        string key,
        string displayName,
        int order,
        bool isTerminal,
        CancellationToken ct = default);

    Task ArchiveWorkflowStateAsync(
        WorkspaceId workspaceId,
        WorkflowStateId stateId,
        WorkflowStateId? replacementStateId,
        CancellationToken ct = default);
}
