namespace Anvilboard.Domain;

/// <summary>
/// Immutable audit-log row recorded for every meaningful mutation. This is the backing data for
/// the dashboard's activity feed and velocity charts, and is what plugin hooks
/// (<c>IIssueHook</c>) are notified about after being persisted.
/// </summary>
public sealed class ActivityEvent
{
    public ActivityEventId Id { get; init; }
    public required IssueId IssueId { get; init; }
    public required ActivityEventType Type { get; init; }
    public MemberId? ActorId { get; init; }

    /// <summary>Optional JSON payload describing the change (e.g. old/new status).</summary>
    public string? DataJson { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

public enum ActivityEventType
{
    Created,
    StatusChanged,
    AssigneeChanged,
    PriorityChanged,
    CommentAdded,
    LabelsChanged,
    SyncedFromExternal,
}
