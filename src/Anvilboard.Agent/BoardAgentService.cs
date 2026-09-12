using Anvilboard.Agent.Authorization;
using Anvilboard.Agent.Automation;
using Anvilboard.Agent.Contracts;
using Anvilboard.Application.Automation;
using Anvilboard.Application.Backup;
using Anvilboard.Application.Dashboard;
using Anvilboard.Application.Issues;
using Anvilboard.Domain;
using DotNetAgentSurface.Core;

namespace Anvilboard.Agent;

/// <summary>
/// Thin <see cref="AgentOperationAttribute"/>-annotated wrapper over <see cref="IssueService"/>,
/// <see cref="DashboardService"/>, and <see cref="IBackupService"/> — the same application services
/// the ASP.NET Core API host calls directly from its minimal API endpoints. Wrapping (rather than
/// annotating the application services themselves) keeps <c>Anvilboard.Application</c> free of any
/// dependency on the agent surface package, and lets this layer shape parameters/results (plain
/// scalars, no EF entities) the way <see cref="OperationCatalog.Discover"/>'s reflection-based
/// binding expects.
/// </summary>
/// <remarks>
/// <para>
/// Every operation carries a <see cref="RequiresAgentPermissionAttribute"/> and runs only after
/// <see cref="WorkspaceAuthorizationPolicy"/> has authenticated the host credential and authorized
/// that permission. Operations therefore never authenticate or authorize themselves — they read
/// the already-resolved actor from <see cref="AgentActorAccessor"/> and attribute their writes to
/// it, which is why no operation accepts a caller-supplied workspace or actor id.
/// </para>
/// <para>
/// Mutating operations require an <c>idempotencyKey</c> so an agent retrying after a timeout does
/// not duplicate work, and every operation returns <see cref="AgentResponse{T}"/> so callers can
/// see the contract version and correlate the invocation with server-side records.
/// </para>
/// </remarks>
public sealed class BoardAgentService(
    IssueService issues,
    IssueLinkService issueLinks,
    DashboardService dashboard,
    IBackupService backups,
    AgentActorAccessor actors,
    AgentWorkspaceScope scope,
    AgentIdempotency idempotency,
    CorrelationContext correlation)
{
    /// <summary>
    /// This process is both a CLI and an MCP server (see <c>Program.cs</c>), but never both at once
    /// for a single invocation — the transport is fixed for the process's whole lifetime by whether
    /// <c>args[0]</c> is <c>"mcp"</c>. Mirroring that exact check (rather than threading a new
    /// per-request signal through DI) lets every <see cref="BackupOperationContext"/> built here
    /// carry the real, explicit <see cref="AuditChannel"/> for the transport actually in use,
    /// consistent with plan §11.4's "must not infer the channel from runtime type or ambient
    /// process mode" — this reads the one ambient signal (argv) that Program.cs itself already
    /// treats as authoritative for choosing the transport, rather than guessing.
    /// </summary>
    private static readonly AuditChannel AgentChannel =
        Environment.GetCommandLineArgs() is [_, "mcp", ..] ? AuditChannel.Mcp : AuditChannel.Cli;

    private BackupOperationContext NewBackupOperationContext() =>
        new(AgentActorId.For(actors.Actor), AgentChannel, correlation.CorrelationId);

    private AgentResponse<T> Ok<T>(T data) => AgentResponse<T>.For(correlation.CorrelationId, data);

    [AgentOperation("list-issues", "Lists issues, optionally filtered by team, status, or assignee", Category = "issues", IsIdempotent = true)]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues, Permission.ReadBoard)]
    public async Task<AgentResponse<IReadOnlyList<IssueSummary>>> ListIssuesAsync(
        Guid? teamId = null, IssueStatus? status = null, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var results = await issues.ListAsync(
            scope.WorkspaceId,
            teamId is { } t ? await scope.RequireTeamAsync(t, cancellationToken) : null,
            status,
            await scope.RequireMemberAsync(assigneeId, cancellationToken),
            cancellationToken);
        return Ok<IReadOnlyList<IssueSummary>>([.. results.Select(IssueSummary.FromIssue)]);
    }

    [AgentOperation("get-issue", "Gets a single issue by id", Category = "issues", IsIdempotent = true)]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues, Permission.ReadBoard)]
    public async Task<AgentResponse<IssueSummary?>> GetIssueAsync(Guid issueId, CancellationToken cancellationToken = default)
    {
        var id = await scope.RequireIssueAsync(issueId, cancellationToken);
        var issue = await issues.GetAsync(scope.WorkspaceId, id, cancellationToken);
        return Ok(issue is null ? null : IssueSummary.FromIssue(issue));
    }

    [AgentOperation("create-issue", "Creates a new issue under a team", Category = "issues",
        Examples = ["create-issue --teamId \"3f2a...\" --title \"Forge the anvil\" --idempotencyKey \"create-anvil-1\""])]
    [RequiresAgentPermission(Permission.ReadWriteIssues)]
    public async Task<AgentResponse<IssueSummary>> CreateIssueAsync(
        Guid teamId,
        string title,
        string idempotencyKey,
        string? description = null,
        IssuePriority priority = IssuePriority.None,
        Guid? assigneeId = null,
        CancellationToken cancellationToken = default)
    {
        var summary = await idempotency.ExecuteAsync(
            "create-issue", idempotencyKey, [teamId, title, description, priority, assigneeId],
            async (actor, ct) =>
            {
                var team = await scope.RequireTeamAsync(teamId, ct);
                var assignee = await scope.RequireMemberAsync(assigneeId, ct);
                var issue = await issues.CreateAsync(
                    scope.WorkspaceId, team, title, description, priority, projectId: null, assignee, actor.MemberId, ct);
                return IssueSummary.FromIssue(issue);
            },
            cancellationToken);

        return Ok(summary);
    }

    [AgentOperation("change-issue-status", "Changes the status of an issue", Category = "issues")]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)]
    public async Task<AgentResponse<IssueSummary>> ChangeIssueStatusAsync(
        Guid issueId, IssueStatus status, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var summary = await idempotency.ExecuteAsync(
            "change-issue-status", idempotencyKey, [issueId, status],
            async (actor, ct) =>
            {
                var id = await scope.RequireIssueAsync(issueId, ct);
                var issue = await issues.ChangeStatusAsync(scope.WorkspaceId, id, status, actor.MemberId, ct);
                return IssueSummary.FromIssue(issue);
            },
            cancellationToken);

        return Ok(summary);
    }

    [AgentOperation("assign-issue", "Assigns (or unassigns, when assigneeId is omitted) an issue", Category = "issues")]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)]
    public async Task<AgentResponse<IssueSummary>> AssignIssueAsync(
        Guid issueId, string idempotencyKey, Guid? assigneeId = null, CancellationToken cancellationToken = default)
    {
        var summary = await idempotency.ExecuteAsync(
            "assign-issue", idempotencyKey, [issueId, assigneeId],
            async (actor, ct) =>
            {
                var id = await scope.RequireIssueAsync(issueId, ct);
                var assignee = await scope.RequireMemberAsync(assigneeId, ct);
                var issue = await issues.AssignAsync(scope.WorkspaceId, id, assignee, actor.MemberId, ct);
                return IssueSummary.FromIssue(issue);
            },
            cancellationToken);

        return Ok(summary);
    }

    [AgentOperation("comment-on-issue", "Adds a comment to an issue", Category = "issues")]
    [RequiresAgentPermission(Permission.ReadWriteComments)]
    public async Task<AgentResponse<CommentSummary>> CommentOnIssueAsync(
        Guid issueId, string body, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var summary = await idempotency.ExecuteAsync(
            "comment-on-issue", idempotencyKey, [issueId, body],
            async (actor, ct) =>
            {
                var id = await scope.RequireIssueAsync(issueId, ct);
                var comment = await issues.AddCommentAsync(scope.WorkspaceId, id, body, actor.MemberId, ct);
                return new CommentSummary(
                    comment.Id.Value, comment.IssueId.Value, comment.AuthorId?.Value, comment.Body, comment.CreatedAt);
            },
            cancellationToken);

        return Ok(summary);
    }

    [AgentOperation("dashboard-summary", "Gets aggregate dashboard counts (by status, by source, velocity, workload)", Category = "dashboard", IsIdempotent = true)]
    [RequiresAgentPermission(Permission.ReadDashboard)]
    public async Task<AgentResponse<DashboardSummary>> DashboardSummaryAsync(Guid? teamId = null, CancellationToken cancellationToken = default)
    {
        var summary = await dashboard.GetSummaryAsync(
            scope.WorkspaceId,
            teamId is { } t ? await scope.RequireTeamAsync(t, cancellationToken) : null,
            cancellationToken);
        return Ok(summary);
    }

    [AgentOperation("list-issue-links", "Lists links involving an issue, in either direction", Category = "issues", IsIdempotent = true)]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues, Permission.ReadBoard)]
    public async Task<AgentResponse<IReadOnlyList<IssueLinkDto>>> ListIssueLinksAsync(Guid issueId, CancellationToken cancellationToken = default) =>
        Ok(await issueLinks.ListLinksAsync(
            scope.WorkspaceId, await scope.RequireIssueAsync(issueId, cancellationToken), cancellationToken));

    [AgentOperation("create-issue-link", "Creates a directional, typed link from one issue to another", Category = "issues",
        Examples = ["create-issue-link --issueId \"3f2a...\" --targetIssueId \"9b1c...\" --type \"blocks\" --idempotencyKey \"link-1\""])]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)]
    public async Task<AgentResponse<IssueLinkDto>> CreateIssueLinkAsync(
        Guid issueId, Guid targetIssueId, string type, string idempotencyKey,
        string? description = null, CancellationToken cancellationToken = default)
    {
        var link = await idempotency.ExecuteAsync(
            "create-issue-link", idempotencyKey, [issueId, targetIssueId, type, description],
            async (actor, ct) => await issueLinks.CreateLinkAsync(
                scope.WorkspaceId,
                await scope.RequireIssueAsync(issueId, ct),
                await scope.RequireIssueAsync(targetIssueId, ct),
                type, description, actor.MemberId, ct),
            cancellationToken);

        return Ok(link);
    }

    [AgentOperation("remove-issue-link", "Removes a link from an issue", Category = "issues")]
    [RequiresAgentPermission(Permission.ReadWriteIssues, Permission.ReadWriteAssignedIssues)]
    public async Task<AgentResponse<LinkRemoval>> RemoveIssueLinkAsync(
        Guid issueId, Guid linkId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        // Returns a value rather than void so a replay has something to return: the idempotency
        // protocol stores and replays a serialized result, which a void operation cannot provide.
        var removal = await idempotency.ExecuteAsync(
            "remove-issue-link", idempotencyKey, [issueId, linkId],
            async (actor, ct) =>
            {
                var id = await scope.RequireIssueAsync(issueId, ct);
                await issueLinks.RemoveLinkAsync(scope.WorkspaceId, id, new IssueLinkId(linkId), actor.MemberId, ct);
                return new LinkRemoval(issueId, linkId);
            },
            cancellationToken);

        return Ok(removal);
    }

    // `restore` is deliberately not exposed here (`docs/plans/agent-surface-authorization.md`
    // DR-AGT-004). Authorization now exists, so the blocker is no longer identity: restore is an
    // instance-wide destructive operation that would need `AgentSafetyLevel.Dangerous` plus an
    // `IConfirmationEnforcingPolicy`, and MCP has no trustworthy interactive confirmation channel —
    // a client can set the confirmed flag itself, so "confirmation" would be self-attested.
    // Restore stays a REST-only operation behind an interactive human confirmation.

    [AgentOperation("create-backup", "Creates a verified snapshot of the instance database",
        Category = "backup", Examples = ["create-backup"])]
    [RequiresAgentPermission(Permission.ManageBackupRestore)]
    public async Task<AgentResponse<BackupManifest>> CreateBackupAsync(CancellationToken cancellationToken = default) =>
        // The workspace comes from the authenticated actor, never from the caller: accepting a
        // workspace id here would let any credential operate on any workspace.
        Ok(await backups.CreateBackupAsync(actors.Actor.WorkspaceId, NewBackupOperationContext(), cancellationToken));

    [AgentOperation("list-backups", "Lists available verified backups, newest first",
        Category = "backup", IsIdempotent = true)]
    [RequiresAgentPermission(Permission.ManageBackupRestore)]
    public async Task<AgentResponse<IReadOnlyList<BackupManifest>>> ListBackupsAsync(CancellationToken cancellationToken = default) =>
        Ok(await backups.ListBackupsAsync(actors.Actor.WorkspaceId, cancellationToken));

    [AgentOperation("verify-backup", "Verifies a backup's integrity without restoring it",
        Category = "backup", IsIdempotent = true, Examples = ["verify-backup --backupId \"7f3c0c4e-0000-4000-8000-000000000000\""])]
    [RequiresAgentPermission(Permission.ManageBackupRestore)]
    public async Task<AgentResponse<BackupVerification>> VerifyBackupAsync(Guid backupId, CancellationToken cancellationToken = default) =>
        Ok(await backups.VerifyBackupAsync(actors.Actor.WorkspaceId, new BackupArtifactRef(backupId), cancellationToken));
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

/// <summary>Confirmation that a link was removed, so the removal has a replayable result.</summary>
public sealed record LinkRemoval(Guid IssueId, Guid LinkId);
