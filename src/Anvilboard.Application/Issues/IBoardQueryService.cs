using Anvilboard.Domain;

namespace Anvilboard.Application.Issues;

public interface IBoardQueryService
{
    Task<BoardQueryResult> QueryAsync(BoardQuery query, CancellationToken ct = default);
}

public sealed record BoardQuery(
    WorkspaceId WorkspaceId,
    WorkflowStateId? WorkflowStateId = null,
    MemberId? AssigneeId = null,
    IntegrationProvider? Provider = null,
    ProjectId? ProjectId = null,
    string? Priority = null,
    string? Type = null,
    LabelId? LabelId = null,
    BoardSyncCondition? SyncCondition = null,
    BoardGroupBy GroupBy = BoardGroupBy.WorkflowState,
    BoardOrderBy OrderBy = BoardOrderBy.CreatedAt,
    int Page = 1,
    int Limit = 25,
    string? Cursor = null,
    bool IncludeArchived = false);

public enum BoardGroupBy
{
    WorkflowState,
    Type,
    Priority,
    Assignee,
    Label,
}

public enum BoardOrderBy
{
    CreatedAt,
    UpdatedAt,
    Priority,
    Manual,
}

public enum BoardSyncCondition
{
    Fresh,
    Stale,
    Paused,
    Failed,
}

public sealed record BoardQueryResult(
    IReadOnlyList<BoardGroup> Groups,
    int TotalCount,
    int Page,
    int Limit,
    string? NextCursor,
    BoardQuery AppliedQuery);

public sealed record BoardGroup(string Key, string DisplayName, IReadOnlyList<BoardIssue> Issues);

public sealed record BoardIssue(
    IssueId Id,
    string Key,
    string Title,
    WorkflowStateId WorkflowStateId,
    string? Type,
    IssuePriority Priority,
    MemberId? AssigneeId,
    ProjectId? ProjectId,
    IntegrationProvider Provider,
    IReadOnlyList<LabelId> LabelIds,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed class BoardQueryException(string message) : InvalidOperationException(message)
{
    public const string ValidationErrorCode = "VALIDATION_FAILED";
    public string ErrorCode => ValidationErrorCode;
}
