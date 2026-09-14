namespace Anvilboard.Domain;

/// <summary>
/// The central aggregate of the whole system: a unit of work on the board. Issues are created
/// either directly by a user/agent (<see cref="IntegrationProvider.Local"/>) or synced in from an
/// external source, in which case exactly one <see cref="ExternalLink"/> row identifies its
/// remote origin and is used to de-duplicate repeated syncs.
/// </summary>
public sealed class Issue
{
    public IssueId Id { get; init; }
    public required TeamId TeamId { get; set; }
    public ProjectId? ProjectId { get; set; }

    /// <summary>Human-readable key such as "ENG-142", built from the owning team's key + number.</summary>
    public required string Key { get; set; }

    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Deprecated legacy status retained while the workflow-state migration is rolled out.
    /// New state changes will be coordinated through <see cref="WorkflowStateId"/> by the
    /// Issue &amp; Board Service.
    /// </summary>
    public IssueStatus Status { get; set; } = IssueStatus.Backlog;

    /// <summary>The required, workspace-configured workflow state for this issue.</summary>
    public WorkflowStateId WorkflowStateId { get; set; }

    /// <summary>
    /// Optimistic-concurrency token. Starts at <c>1</c> on create and is incremented by every
    /// field-level mutation, so a caller can pass the value it read as <c>expectedVersion</c> and
    /// have a stale write rejected with <c>CONCURRENCY_CONFLICT</c> rather than silently applied.
    /// Two paths are deliberately exempt and leave it untouched: adding a comment (which does not
    /// change a field anyone conditions a write on) and the external-ingestion upsert (whose
    /// authority is the upstream provider, not a local read).
    /// </summary>
    public int Version { get; set; }

    public IssuePriority Priority { get; set; } = IssuePriority.None;

    /// <summary>Optional free-form issue category used for board filtering and grouping.</summary>
    public string? Type { get; set; }

    /// <summary>Set when the issue is hidden from normal board and list queries.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    public MemberId? AssigneeId { get; set; }
    public MemberId? CreatedById { get; set; }

    /// <summary>
    /// Where this issue originated. <see cref="IntegrationProvider.Local"/> for issues created
    /// directly in Anvilboard; otherwise set by the ingestion pipeline that created it.
    /// </summary>
    public IntegrationProvider Source { get; set; } = IntegrationProvider.Local;

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public List<LabelId> LabelIds { get; init; } = [];
}
