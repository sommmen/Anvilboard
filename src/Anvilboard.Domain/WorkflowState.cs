namespace Anvilboard.Domain;

/// <summary>
/// A single ordered step in a workspace's configurable workflow (e.g. "in progress"), replacing
/// the fixed <see cref="IssueStatus"/> enum with a per-workspace set that administrators can
/// extend without a code change. <see cref="Key"/> is the stable, lower-snake identifier
/// referenced by <see cref="WorkflowTransition"/> and (eventually) <c>Issue.WorkflowStateId</c>;
/// external REST/CLI/MCP responses serialize it as <c>Key.ToUpperInvariant()</c>
/// (<c>docs/features/workflow-engine.md</c> "Symbolic serialization").
/// </summary>
public sealed class WorkflowState
{
    public WorkflowStateId Id { get; init; }
    public required WorkspaceId WorkspaceId { get; set; }

    /// <summary>Stable lower-snake identifier, unique per workspace, e.g. "in_progress".</summary>
    public required string Key { get; set; }

    public required string DisplayName { get; set; }

    /// <summary>Board/list column ordering; not used to infer transition adjacency.</summary>
    public int Order { get; set; }

    /// <summary>True for a state such as "done" or "cancelled" that ends an issue's lifecycle.</summary>
    public bool IsTerminal { get; set; }

    /// <summary>
    /// True once archived via <c>WorkflowEngine.ArchiveWorkflowStateAsync</c>. An archived state
    /// can no longer be a valid current or target state for a transition.
    /// </summary>
    public bool IsArchived { get; set; }
}
