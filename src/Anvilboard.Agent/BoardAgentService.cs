using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent;

/// <summary>
/// Thin <see cref="AgentOperationAttribute"/>-annotated wrapper over <see cref="IssueService"/> and
/// <see cref="DashboardService"/> — the same application services the ASP.NET Core API host calls
/// directly from its minimal API endpoints. Wrapping (rather than annotating the application
/// services themselves) keeps <c>Anvilboard.Application</c> free of any dependency on the agent
/// surface package, and lets this layer shape parameters/results (plain scalars, no EF entities)
/// the way <see cref="OperationCatalog.Discover"/>'s reflection-based binding expects.
/// </summary>
public sealed class BoardAgentService(IssueService issues, DashboardService dashboard)
{
    [AgentOperation("list-issues", "Lists issues, optionally filtered by team, status, or assignee", Category = "issues", IsIdempotent = true)]
    public async Task<IReadOnlyList<IssueSummary>> ListIssuesAsync(
        Guid? teamId = null, IssueStatus? status = null, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var results = await issues.ListAsync(
            teamId is { } t ? new TeamId(t) : null,
            status,
            assigneeId is { } a ? new MemberId(a) : null,
            cancellationToken);
        return [.. results.Select(IssueSummary.FromIssue)];
    }

    [AgentOperation("get-issue", "Gets a single issue by id", Category = "issues", IsIdempotent = true)]
    public async Task<IssueSummary?> GetIssueAsync(Guid issueId, CancellationToken cancellationToken = default)
    {
        var issue = await issues.GetAsync(new IssueId(issueId), cancellationToken);
        return issue is null ? null : IssueSummary.FromIssue(issue);
    }

    [AgentOperation("create-issue", "Creates a new issue under a team", Category = "issues", Examples = ["create-issue teamId=... title=\"Forge the anvil\""])]
    public async Task<IssueSummary> CreateIssueAsync(
        Guid teamId,
        string title,
        string? description = null,
        IssuePriority priority = IssuePriority.None,
        Guid? assigneeId = null,
        CancellationToken cancellationToken = default)
    {
        var issue = await issues.CreateAsync(
            new TeamId(teamId), title, description, priority, projectId: null,
            assigneeId is { } a ? new MemberId(a) : null, createdById: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("change-issue-status", "Changes the status of an issue", Category = "issues")]
    public async Task<IssueSummary> ChangeIssueStatusAsync(Guid issueId, IssueStatus status, CancellationToken cancellationToken = default)
    {
        var issue = await issues.ChangeStatusAsync(new IssueId(issueId), status, actorId: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("assign-issue", "Assigns (or unassigns, when assigneeId is omitted) an issue", Category = "issues")]
    public async Task<IssueSummary> AssignIssueAsync(Guid issueId, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var issue = await issues.AssignAsync(
            new IssueId(issueId), assigneeId is { } a ? new MemberId(a) : null, actorId: null, cancellationToken);
        return IssueSummary.FromIssue(issue);
    }

    [AgentOperation("comment-on-issue", "Adds a comment to an issue", Category = "issues")]
    public async Task<CommentSummary> CommentOnIssueAsync(Guid issueId, string body, CancellationToken cancellationToken = default)
    {
        var comment = await issues.AddCommentAsync(new IssueId(issueId), body, authorId: null, cancellationToken);
        return new CommentSummary(comment.Id.Value, comment.IssueId.Value, comment.AuthorId?.Value, comment.Body, comment.CreatedAt);
    }

    [AgentOperation("dashboard-summary", "Gets aggregate dashboard counts (by status, by source, velocity, workload)", Category = "dashboard", IsIdempotent = true)]
    public async Task<DashboardSummary> DashboardSummaryAsync(Guid? teamId = null, CancellationToken cancellationToken = default)
    {
        return await dashboard.GetSummaryAsync(teamId is { } t ? new TeamId(t) : null, cancellationToken);
    }
}

public sealed record IssueSummary(
    Guid Id,
    Guid TeamId,
    string Key,
    string Title,
    string? Description,
    IssueStatus Status,
    IssuePriority Priority,
    Guid? AssigneeId,
    IntegrationProvider Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt)
{
    public static IssueSummary FromIssue(Issue issue) => new(
        issue.Id.Value, issue.TeamId.Value, issue.Key, issue.Title, issue.Description,
        issue.Status, issue.Priority, issue.AssigneeId?.Value, issue.Source,
        issue.CreatedAt, issue.UpdatedAt, issue.CompletedAt);
}

public sealed record CommentSummary(Guid Id, Guid IssueId, Guid? AuthorId, string Body, DateTimeOffset CreatedAt);
