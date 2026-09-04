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

    public IssueStatus Status { get; set; } = IssueStatus.Backlog;
    public IssuePriority Priority { get; set; } = IssuePriority.None;

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
