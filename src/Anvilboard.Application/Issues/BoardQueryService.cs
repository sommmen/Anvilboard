using System.Text;
using System.Text.Json;
using Anvilboard.Domain;
using Anvilboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Anvilboard.Application.Issues;

public sealed class BoardQueryService(AnvilboardDbContext db) : IBoardQueryService
{
    private const int DefaultLimit = 25;
    private const int MaximumLimit = 100;

    public async Task<BoardQueryResult> QueryAsync(BoardQuery query, CancellationToken ct = default)
    {
        Validate(query);

        // Sync health is supplied by the integration-platform slice. Until that model exists, a
        // requested condition intentionally matches nothing rather than inventing health state.
        if (query.SyncCondition is not null)
        {
            return new BoardQueryResult([], 0, query.Page, query.Limit, null, query);
        }

        var issuesQuery = db.Issues.AsNoTracking()
            .Join(db.Teams.AsNoTracking(), issue => issue.TeamId, team => team.Id, (issue, team) => new { Issue = issue, team.WorkspaceId })
            .Where(row => row.WorkspaceId == query.WorkspaceId)
            .Select(row => row.Issue);

        if (!query.IncludeArchived)
        {
            issuesQuery = issuesQuery.Where(issue => issue.ArchivedAt == null);
        }

        if (query.WorkflowStateId is { } workflowStateId)
        {
            issuesQuery = issuesQuery.Where(issue => issue.WorkflowStateId == workflowStateId);
        }

        if (query.AssigneeId is { } assigneeId)
        {
            issuesQuery = issuesQuery.Where(issue => issue.AssigneeId == assigneeId);
        }

        if (query.Provider is { } provider)
        {
            issuesQuery = issuesQuery.Where(issue => issue.Source == provider);
        }

        if (query.ProjectId is { } projectId)
        {
            issuesQuery = issuesQuery.Where(issue => issue.ProjectId == projectId);
        }

        if (!string.IsNullOrWhiteSpace(query.Priority))
        {
            if (!Enum.TryParse<IssuePriority>(query.Priority, true, out var priority))
            {
                return Empty(query);
            }

            issuesQuery = issuesQuery.Where(issue => issue.Priority == priority);
        }

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var type = query.Type.Trim();
            issuesQuery = issuesQuery.Where(issue => issue.Type != null && EF.Functions.Like(issue.Type, type));
        }

        var issues = await issuesQuery.ToListAsync(ct);

        if (query.LabelId is { } labelId)
        {
            issues = issues.Where(issue => issue.LabelIds.Contains(labelId)).ToList();
        }

        var ordered = Order(issues, query.OrderBy).ToList();
        var cursorOffset = DecodeCursor(query.Cursor, ordered.Count);
        var offset = cursorOffset ?? checked((query.Page - 1) * query.Limit);
        var pageIssues = ordered.Skip(offset).Take(query.Limit).ToList();
        var nextOffset = offset + pageIssues.Count;
        var nextCursor = nextOffset < ordered.Count ? EncodeCursor(nextOffset) : null;

        var workflowStates = await db.WorkflowStates.AsNoTracking()
            .Where(state => state.WorkspaceId == query.WorkspaceId)
            .ToDictionaryAsync(state => state.Id, ct);
        var members = await db.Members.AsNoTracking()
            .Where(member => member.WorkspaceId == query.WorkspaceId)
            .ToDictionaryAsync(member => member.Id, ct);
        var labels = await db.Labels.AsNoTracking()
            .Where(label => label.WorkspaceId == query.WorkspaceId)
            .ToDictionaryAsync(label => label.Id, ct);

        return new BoardQueryResult(
            Group(pageIssues, query.GroupBy, workflowStates, members, labels),
            ordered.Count,
            query.Page,
            query.Limit,
            nextCursor,
            query);
    }

    private static BoardQueryResult Empty(BoardQuery query) => new([], 0, query.Page, query.Limit, null, query);

    private static void Validate(BoardQuery query)
    {
        if (query.Page < 1)
        {
            throw new BoardQueryException("Page must be at least 1.");
        }

        if (query.Limit is < 1 or > MaximumLimit)
        {
            throw new BoardQueryException($"Limit must be between 1 and {MaximumLimit}.");
        }
    }

    private static IEnumerable<Issue> Order(IEnumerable<Issue> issues, BoardOrderBy orderBy) => orderBy switch
    {
        BoardOrderBy.UpdatedAt => issues.OrderByDescending(issue => issue.UpdatedAt).ThenByDescending(issue => issue.CreatedAt).ThenBy(issue => issue.Id.Value),
        BoardOrderBy.Priority => issues.OrderByDescending(issue => issue.Priority).ThenByDescending(issue => issue.CreatedAt).ThenBy(issue => issue.Id.Value),
        BoardOrderBy.Manual => issues.OrderByDescending(issue => issue.CreatedAt).ThenBy(issue => issue.Id.Value),
        _ => issues.OrderByDescending(issue => issue.CreatedAt).ThenBy(issue => issue.Id.Value),
    };

    private static IReadOnlyList<BoardGroup> Group(
        IReadOnlyList<Issue> issues,
        BoardGroupBy groupBy,
        IReadOnlyDictionary<WorkflowStateId, WorkflowState> workflowStates,
        IReadOnlyDictionary<MemberId, Member> members,
        IReadOnlyDictionary<LabelId, Label> labels)
    {
        return groupBy switch
        {
            BoardGroupBy.Type => issues.GroupBy(issue => issue.Type ?? "").OrderBy(group => string.IsNullOrEmpty(group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).Select(group => ToGroup(group.Key, string.IsNullOrEmpty(group.Key) ? "Untyped" : group.Key, group)).ToList(),
            BoardGroupBy.Priority => issues.GroupBy(issue => issue.Priority).OrderBy(group => group.Key == IssuePriority.None).ThenByDescending(group => group.Key).Select(group => ToGroup(group.Key.ToString(), group.Key == IssuePriority.None ? "No priority" : group.Key.ToString(), group)).ToList(),
            BoardGroupBy.Assignee => issues.GroupBy(issue => issue.AssigneeId).OrderBy(group => group.Key is null).ThenBy(group => group.Key?.Value).Select(group => ToGroup(group.Key?.ToString() ?? "", group.Key is null ? "Unassigned" : members.TryGetValue(group.Key.Value, out var member) ? member.DisplayName : group.Key.Value.ToString(), group)).ToList(),
            BoardGroupBy.Label => GroupByLabel(issues, labels),
            _ => issues.GroupBy(issue => issue.WorkflowStateId).OrderBy(group => workflowStates.TryGetValue(group.Key, out var state) ? state.Order : int.MaxValue).ThenBy(group => group.Key.Value).Select(group => ToGroup(group.Key.ToString(), workflowStates.TryGetValue(group.Key, out var state) ? state.DisplayName : group.Key.ToString(), group)).ToList(),
        };
    }

    private static IReadOnlyList<BoardGroup> GroupByLabel(IReadOnlyList<Issue> issues, IReadOnlyDictionary<LabelId, Label> labels)
    {
        var groups = new Dictionary<string, List<Issue>>();
        foreach (var issue in issues)
        {
            IEnumerable<string?> labelIds = issue.LabelIds.Count == 0
                ? [null]
                : issue.LabelIds.Select(id => id.ToString());
            foreach (var labelKey in labelIds)
            {
                var key = labelKey ?? string.Empty;
                if (!groups.TryGetValue(key, out var labelled))
                {
                    labelled = [];
                    groups[key] = labelled;
                }

                labelled.Add(issue);
            }
        }

        return groups.OrderBy(group => string.IsNullOrEmpty(group.Key))
            .ThenBy(group => LabelName(group.Key, labels), StringComparer.OrdinalIgnoreCase)
            .Select(group => ToGroup(group.Key, string.IsNullOrEmpty(group.Key) ? "No label" : LabelName(group.Key, labels), group.Value))
            .ToList();
    }

    private static string LabelName(string labelKey, IReadOnlyDictionary<LabelId, Label> labels) =>
        Guid.TryParse(labelKey, out var labelId) && labels.TryGetValue(new LabelId(labelId), out var label)
            ? label.Name
            : labelKey;

    private static BoardGroup ToGroup(string key, string displayName, IEnumerable<Issue> issues) =>
        new(key, displayName, issues.Select(ToBoardIssue).ToList());

    private static BoardIssue ToBoardIssue(Issue issue) => new(issue.Id, issue.Key, issue.Title, issue.WorkflowStateId, issue.Type, issue.Priority, issue.AssigneeId, issue.ProjectId, issue.Source, issue.LabelIds, issue.CreatedAt, issue.UpdatedAt, issue.ArchivedAt);

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Cursor(offset))));

    private static int? DecodeCursor(string? cursor, int total)
    {
        if (cursor is null)
        {
            return null;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var value = JsonSerializer.Deserialize<Cursor>(decoded);
            if (value is null || value.Offset < 0 || value.Offset > total)
            {
                throw new BoardQueryException("Cursor is malformed.");
            }

            return value.Offset;
        }
        catch (FormatException)
        {
            throw new BoardQueryException("Cursor is malformed.");
        }
        catch (JsonException)
        {
            throw new BoardQueryException("Cursor is malformed.");
        }
    }

    private sealed record Cursor(int Offset);
}
