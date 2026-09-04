namespace Anvilboard.Domain;

/// <summary>
/// Workflow state of an <see cref="Issue"/>. Kept as a fixed, ordered enum (rather than a
/// per-team configurable state table) to stay true to the "low-resource, minimal" goal: a single
/// SQLite integer column, no join needed to render a board. <see cref="Category"/> groups states
/// into the board columns that ingestion/reporting code reasons about.
/// </summary>
public enum IssueStatus
{
    Backlog = 0,
    Todo = 1,
    InProgress = 2,
    InReview = 3,
    Done = 4,
    Cancelled = 5,
}

public static class IssueStatusExtensions
{
    public static IssueStatusCategory Category(this IssueStatus status) => status switch
    {
        IssueStatus.Backlog => IssueStatusCategory.Backlog,
        IssueStatus.Todo => IssueStatusCategory.Unstarted,
        IssueStatus.InProgress or IssueStatus.InReview => IssueStatusCategory.Started,
        IssueStatus.Done => IssueStatusCategory.Completed,
        IssueStatus.Cancelled => IssueStatusCategory.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static bool IsTerminal(this IssueStatus status) =>
        status is IssueStatus.Done or IssueStatus.Cancelled;
}

/// <summary>Coarse grouping used by dashboard aggregation and board column ordering.</summary>
public enum IssueStatusCategory
{
    Backlog,
    Unstarted,
    Started,
    Completed,
    Cancelled,
}

public enum IssuePriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4,
}
