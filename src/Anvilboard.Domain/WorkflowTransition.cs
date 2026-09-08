namespace Anvilboard.Domain;

/// <summary>
/// One allowed edge in a workspace's workflow adjacency list: an issue may move from
/// <see cref="FromStateId"/> to <see cref="ToStateId"/>. <see cref="WorkflowEngine"/> validates
/// every requested transition against this table rather than any hardcoded ordering
/// (<c>docs/anvilboard/tech-design.md</c> §7.5).
/// </summary>
public sealed class WorkflowTransition
{
    public WorkflowTransitionId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; set; }
    public required WorkflowStateId FromStateId { get; set; }
    public required WorkflowStateId ToStateId { get; set; }
}
